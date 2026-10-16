using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;

/// <summary>Bounded MSB-first reader for the bit-packed drawing structures in the SWF 19 specification.</summary>
internal sealed class ArtworkReader
{
    private readonly byte[] data;
    private int bit;
    public int Position { get => (bit + 7) / 8; set { Require(value, 0); bit = checked(value * 8); } }
    public int Length => data.Length;
    public ArtworkReader(ReadOnlyMemory<byte> payload) => data = payload.ToArray();
    public void Align() => bit = checked(Position * 8);
    public uint Bits(int count)
    {
        if (count is < 0 or > 32 || (long)bit + count > (long)data.Length * 8)
            throw new InvalidDataException("The artwork bit stream is truncated.");
        uint result = 0;
        for (int i = 0; i < count; i++, bit++) result = (result << 1) | (uint)((data[bit >> 3] >> (7 - (bit & 7))) & 1);
        return result;
    }
    public int Signed(int count)
    {
        if (count == 0) return 0;
        uint value = Bits(count);
        return count == 32 ? unchecked((int)value) : (int)(value << (32 - count)) >> (32 - count);
    }
    public byte U8() { Align(); Require(Position, 1); byte v = data[Position]; bit += 8; return v; }
    public ushort U16() { Align(); Require(Position, 2); ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position)); bit += 16; return v; }
    public short I16() => unchecked((short)U16());
    public uint U32() { Align(); Require(Position, 4); uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position)); bit += 32; return v; }
    public byte[] Bytes(int count) { Align(); Require(Position, count); var v = data.AsSpan(Position, count).ToArray(); bit += checked(count * 8); return v; }
    public string SizedString() => Encoding.UTF8.GetString(Bytes(U8()));
    public string String()
    {
        Align(); int start = Position, end = Array.IndexOf(data, (byte)0, start);
        if (end < 0) throw new InvalidDataException("The artwork string is not terminated.");
        Position = end + 1;
        return Encoding.UTF8.GetString(data, start, end - start);
    }
    public Color Color(bool alpha) { byte r = U8(), g = U8(), b = U8(); return System.Windows.Media.Color.FromArgb(alpha ? U8() : (byte)255, r, g, b); }
    public Rect Rectangle()
    {
        Align(); int n = (int)Bits(5), x1 = Signed(n), x2 = Signed(n), y1 = Signed(n), y2 = Signed(n); Align();
        if (x2 < x1 || y2 < y1) throw new InvalidDataException("The artwork bounds are inverted.");
        return new Rect(x1 / 20.0, y1 / 20.0, ((double)x2 - x1) / 20, ((double)y2 - y1) / 20);
    }
    public Matrix Matrix()
    {
        Align(); double a = 1, d = 1, b = 0, c = 0;
        if (Bits(1) != 0) { int n = (int)Bits(5); a = Signed(n) / 65536.0; d = Signed(n) / 65536.0; }
        if (Bits(1) != 0) { int n = (int)Bits(5); b = Signed(n) / 65536.0; c = Signed(n) / 65536.0; }
        int t = (int)Bits(5); double x = Signed(t) / 20.0, y = Signed(t) / 20.0; Align();
        return new Matrix(a, b, c, d, x, y);
    }
    private void Require(int start, int count)
    {
        if (start < 0 || count < 0 || start > data.Length - count) throw new InvalidDataException("The artwork record is truncated.");
    }
}
