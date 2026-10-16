using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LegendaryExplorerCore.Scaleform;

/// <summary>An affine transform in pixels, using the same component order as WPF Matrix.</summary>
public readonly record struct ScaleformMatrix(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static ScaleformMatrix Identity => new(1, 0, 0, 1, 0, 0);
}

/// <summary>SWF channel multipliers and offsets. Offsets are in the 0..255 channel space.</summary>
public readonly record struct ScaleformColorTransform(double RedMultiplier, double GreenMultiplier, double BlueMultiplier,
    double AlphaMultiplier, double RedOffset, double GreenOffset, double BlueOffset, double AlphaOffset)
{
    public static ScaleformColorTransform Identity => new(1, 1, 1, 1, 0, 0, 0, 0);
}

/// <summary>
/// The editable prefix of a placement. Color transforms, filters, events and all other
/// trailing data remain opaque when its matrix is changed. Layout follows Adobe's
/// SWF File Format Specification, MATRIX and PlaceObject/2/3.
/// </summary>
public sealed class ScaleformPlacement
{
    public ScaleformTag Tag { get; private init; }
    public ushort? CharacterId { get; private init; }
    public ushort Depth { get; private init; }
    public string InstanceName { get; private init; }
    public ScaleformMatrix? Matrix { get; private init; }
    public ScaleformColorTransform? ColorTransform { get; private init; }
    public bool IsMove { get; private init; }
    public ushort? ClipDepth { get; private init; }
    public bool HasEffects { get; private init; }
    public bool? Visible { get; private init; }
    internal bool HasColorTransform { get; private init; }
    internal bool HasOtherEffects { get; private init; }
    private int MatrixOffset { get; init; }
    private int MatrixLength { get; init; }

    /// <summary>Returns null for a non-placement tag; malformed supported placements throw.</summary>
    public static ScaleformPlacement Read(ScaleformTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return Read(tag, tag.Payload.Span);
    }

    private static ScaleformPlacement Read(ScaleformTag tag, ReadOnlySpan<byte> data)
    {
        if (tag.Code is not (4 or 26 or 70)) return null;
        int offset = 0;
        byte flags = 6, flags2 = 0;
        ushort? character = null;
        if (tag.Code == 4)
        {
            character = UInt16(data, ref offset);
        }
        else
        {
            Require(data, offset, tag.Code == 70 ? 2 : 1);
            flags = data[offset++];
            if (tag.Code == 70) flags2 = data[offset++];
        }
        ushort depth = UInt16(data, ref offset);
        // Runtime readers correct an Adobe specification erratum here: an image
        // with a CharacterId does not also carry an implicit class-name string.
        if ((flags2 & 8) != 0 || (flags2 & 16) != 0 && (flags & 2) == 0)
            _ = CString(data, ref offset); // PlaceObject3 class name precedes CharacterId.
        if (tag.Code != 4 && (flags & 2) != 0)
            character = UInt16(data, ref offset);
        int matrixOffset = offset;
        ScaleformMatrix? matrix = null;
        if ((flags & 4) != 0)
            matrix = ReadMatrix(data, ref offset);
        int matrixLength = offset - matrixOffset;
        string name = null;
        ushort? clipDepth = null;
        ScaleformColorTransform? color = null;
        bool? visible = null;
        if (tag.Code == 4 && offset < data.Length)
            color = ReadColorTransform(data, ref offset, false);
        if (tag.Code != 4)
        {
            if ((flags & 8) != 0)
                color = ReadColorTransform(data, ref offset, true);
            if ((flags & 16) != 0)
                _ = UInt16(data, ref offset); // Morph ratio.
            if ((flags & 32) != 0)
                name = CString(data, ref offset);
            if ((flags & 64) != 0)
                clipDepth = UInt16(data, ref offset);
            if ((flags2 & 32) != 0)
            {
                if ((flags2 & 1) != 0) SkipFilters(data, ref offset);
                if ((flags2 & 2) != 0) { Require(data, offset, 1); offset++; }
                if ((flags2 & 4) != 0) { Require(data, offset, 1); offset++; }
                Require(data, offset, 1);
                visible = data[offset] != 0;
            }
        }
        return new ScaleformPlacement
        {
            Tag = tag, CharacterId = character, Depth = depth, InstanceName = name,
            Matrix = matrix, ColorTransform = color, IsMove = tag.Code != 4 && (flags & 1) != 0,
            ClipDepth = clipDepth, HasEffects = color.HasValue && color != ScaleformColorTransform.Identity || (flags2 & 3) != 0,
            Visible = visible, HasColorTransform = color.HasValue, HasOtherEffects = (flags2 & 3) != 0,
            MatrixOffset = matrixOffset, MatrixLength = matrixLength
        };
    }

    internal static byte[] WithMatrix(ScaleformTag tag, ReadOnlySpan<byte> original, ScaleformMatrix matrix)
    {
        var placement = Read(tag, original) ?? throw new InvalidOperationException("Only PlaceObject, PlaceObject2 and PlaceObject3 contain placement matrices.");
        byte[] encoded = EncodeMatrix(matrix);
        int matrixReadOffset = 0;
        var quantized = ReadMatrix(encoded, ref matrixReadOffset);
        // Reverting an edit restores the original bit widths, padding and flag bytes too.
        if (placement.Matrix == quantized)
            return original.ToArray();
        byte[] edited = new byte[checked(original.Length - placement.MatrixLength + encoded.Length)];
        original[..placement.MatrixOffset].CopyTo(edited);
        encoded.CopyTo(edited, placement.MatrixOffset);
        original[(placement.MatrixOffset + placement.MatrixLength)..].CopyTo(edited.AsSpan(placement.MatrixOffset + encoded.Length));
        if (tag.Code != 4) edited[0] |= 4;
        return edited;
    }

    internal static ScaleformMatrix ReadMatrix(ReadOnlySpan<byte> data, ref int offset)
    {
        var bits = new PlacementBits(data, offset);
        double sx = 1, sy = 1, r0 = 0, r1 = 0;
        if (bits.Unsigned(1) != 0)
        {
            int count = bits.Unsigned(5);
            sx = bits.Signed(count) / 65536.0;
            sy = bits.Signed(count) / 65536.0;
        }
        if (bits.Unsigned(1) != 0)
        {
            int count = bits.Unsigned(5);
            r0 = bits.Signed(count) / 65536.0;
            r1 = bits.Signed(count) / 65536.0;
        }
        int translateBits = bits.Unsigned(5);
        double x = bits.Signed(translateBits) / 20.0, y = bits.Signed(translateBits) / 20.0;
        offset = bits.ByteOffset;
        return new ScaleformMatrix(sx, r0, r1, sy, x, y);
    }

    internal static byte[] EncodeMatrix(ScaleformMatrix matrix)
    {
        int sx = Quantize(matrix.M11, 65536), sy = Quantize(matrix.M22, 65536);
        int r0 = Quantize(matrix.M12, 65536), r1 = Quantize(matrix.M21, 65536);
        int x = Quantize(matrix.OffsetX, 20), y = Quantize(matrix.OffsetY, 20);
        var bits = new PlacementBitWriter();
        bool scale = sx != 65536 || sy != 65536;
        bits.Write(scale ? 1 : 0, 1);
        if (scale)
        {
            int count = Math.Max(SignedWidth(sx), SignedWidth(sy));
            bits.Write(count, 5); bits.Write(sx, count); bits.Write(sy, count);
        }
        bool rotate = r0 != 0 || r1 != 0;
        bits.Write(rotate ? 1 : 0, 1);
        if (rotate)
        {
            int count = Math.Max(SignedWidth(r0), SignedWidth(r1));
            bits.Write(count, 5); bits.Write(r0, count); bits.Write(r1, count);
        }
        int translateBits = x == 0 && y == 0 ? 0 : Math.Max(SignedWidth(x), SignedWidth(y));
        bits.Write(translateBits, 5); bits.Write(x, translateBits); bits.Write(y, translateBits);
        return bits.ToArray();
    }

    private static int Quantize(double value, double factor)
    {
        double encoded = Math.Round(value * factor, MidpointRounding.AwayFromZero);
        // MATRIX declares widths in UB[5], so 31 is its largest signed field width.
        if (!double.IsFinite(encoded) || encoded < -(1L << 30) || encoded > (1L << 30) - 1)
            throw new ArgumentOutOfRangeException(nameof(value), "The transform exceeds SWF MATRIX's signed 31-bit range.");
        return (int)encoded;
    }

    private static int SignedWidth(int value)
    {
        for (int count = 1; count < 31; count++)
            if (value >= -(1 << (count - 1)) && value < 1 << (count - 1)) return count;
        return 31;
    }

    private static ScaleformColorTransform ReadColorTransform(ReadOnlySpan<byte> data, ref int offset, bool alpha)
    {
        var bits = new PlacementBits(data, offset);
        bool add = bits.Unsigned(1) != 0, multiply = bits.Unsigned(1) != 0;
        int width = bits.Unsigned(4);
        double red = 1, green = 1, blue = 1, opacity = 1;
        int redOffset = 0, greenOffset = 0, blueOffset = 0, alphaOffset = 0;
        if (multiply)
        {
            red = bits.Signed(width) / 256.0;
            green = bits.Signed(width) / 256.0;
            blue = bits.Signed(width) / 256.0;
            if (alpha) opacity = bits.Signed(width) / 256.0;
        }
        if (add)
        {
            redOffset = bits.Signed(width); greenOffset = bits.Signed(width); blueOffset = bits.Signed(width);
            if (alpha) alphaOffset = bits.Signed(width);
        }
        offset = bits.ByteOffset;
        return new ScaleformColorTransform(red, green, blue, opacity, redOffset, greenOffset, blueOffset, alphaOffset);
    }

    private static void SkipFilters(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 1);
        int count = data[offset++];
        for (int i = 0; i < count; i++)
        {
            Require(data, offset, 1);
            byte kind = data[offset++];
            int length;
            switch (kind)
            {
                case 0: length = 23; break; // Drop shadow.
                case 1: length = 9; break; // Blur.
                case 2: length = 15; break; // Glow.
                case 3: length = 27; break; // Bevel.
                case 4 or 7:
                    Require(data, offset, 1);
                    length = 20 + 5 * data[offset]; // Gradient glow/bevel.
                    break;
                case 5:
                    Require(data, offset, 2);
                    length = 15 + 4 * data[offset] * data[offset + 1]; // Convolution.
                    break;
                case 6: length = 80; break; // Color matrix.
                default: throw new InvalidDataException($"Unknown placement filter type {kind}.");
            }
            Require(data, offset, length);
            offset += length;
        }
    }

    private static ushort UInt16(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 2);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        return value;
    }

    private static string CString(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 1);
        int count = data[offset..].IndexOf((byte)0);
        if (count < 0) throw new InvalidDataException("A placement string is not null terminated.");
        string value = Encoding.UTF8.GetString(data.Slice(offset, count));
        offset += count + 1;
        return value;
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (offset < 0 || data.Length - offset < count)
            throw new InvalidDataException("The placement record is truncated.");
    }

    private ref struct PlacementBits
    {
        private readonly ReadOnlySpan<byte> data;
        private int position;
        public int ByteOffset => (position + 7) / 8;
        public PlacementBits(ReadOnlySpan<byte> data, int offset) { this.data = data; position = checked(offset * 8); }
        public int Unsigned(int width)
        {
            if (width < 0 || width > 31 || (long)position + width > (long)data.Length * 8)
                throw new InvalidDataException("A placement bit field is truncated.");
            int value = 0;
            for (int i = 0; i < width; i++, position++)
                value = (value << 1) | ((data[position / 8] >> (7 - position % 8)) & 1);
            return value;
        }
        public int Signed(int width)
        {
            int value = Unsigned(width);
            return width == 0 ? 0 : (value << (32 - width)) >> (32 - width);
        }
    }

    private sealed class PlacementBitWriter
    {
        private readonly List<byte> data = new();
        private int position;
        public void Write(int value, int width)
        {
            for (int i = width - 1; i >= 0; i--, position++)
            {
                if ((position & 7) == 0) data.Add(0);
                data[^1] |= (byte)(((value >> i) & 1) << (7 - position % 8));
            }
        }
        public byte[] ToArray() => data.ToArray();
    }
}
