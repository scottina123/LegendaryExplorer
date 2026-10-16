using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace LegendaryExplorerCore.Scaleform;

/// <summary>
/// A preservation-first SWF/Scaleform container. Unedited records are copied verbatim,
/// including proprietary tags. This is structural validation, not a Flash runtime.
/// SWF layouts follow Adobe's SWF File Format Specification v19 (headers, tags, text).
/// </summary>
public sealed partial class ScaleformDocument
{
    public const int MaximumFileLength = 256 * 1024 * 1024;
    internal const int MaximumDepth = 64;
    internal const int MaximumTagCount = 1_000_000;

    private readonly byte[] original;
    private readonly byte[] movieHeader;
    private readonly byte[] tail;
    private readonly ushort originalFrameRate;
    private ushort frameRate;
    private readonly int frameRateOffset;
    private readonly List<ScaleformTag> timelineTags;
    private bool timelineModified;

    public string Signature { get; }
    public byte Version { get; }
    public ushort FrameCount { get; }
    public double StageWidth { get; }
    public double StageHeight { get; }
    public IReadOnlyList<ScaleformTag> Tags { get; }
    public bool IsModified => timelineModified || frameRate != originalFrameRate || Tags.Any(t => t.IsModified);

    /// <summary>Frames per second, stored as the SWF unsigned 8.8 fixed-point value.</summary>
    public double FrameRate
    {
        get => frameRate / 256.0;
        set
        {
            if (!double.IsFinite(value) || value <= 0 || value > ushort.MaxValue / 256.0)
                throw new ArgumentOutOfRangeException(nameof(value), "Frame rate must be greater than zero and at most 255.99609375.");
            int encoded = checked((int)Math.Round(value * 256, MidpointRounding.AwayFromZero));
            if (encoded == 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Frame rate is too small for the SWF 8.8 representation.");
            frameRate = (ushort)encoded;
        }
    }

    private ScaleformDocument(byte[] original, byte[] body, string signature, byte version)
    {
        this.original = original;
        Signature = signature;
        Version = version;
        int rectangleLength = ReadRectangle(body, 0, out int xmin, out int xmax, out int ymin, out int ymax);
        if (xmax < xmin || ymax < ymin)
            throw new InvalidDataException("The movie stage rectangle has inverted bounds.");
        if (body.Length - rectangleLength < 4)
            throw new InvalidDataException("The SWF frame header is truncated.");
        StageWidth = ((double)xmax - xmin) / 20;
        StageHeight = ((double)ymax - ymin) / 20;
        frameRateOffset = rectangleLength;
        originalFrameRate = frameRate = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(rectangleLength));
        FrameCount = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(rectangleLength + 2));
        int headerLength = rectangleLength + 4;
        movieHeader = body.AsSpan(0, headerLength).ToArray();
        int tagCount = 0;
        timelineTags = ParseTags(body.AsMemory(headerLength), 8 + headerLength, version, 0, ref tagCount, out tail).ToList();
        Tags = timelineTags.AsReadOnly();
    }

    /// <summary>Parses FWS/CWS and Scaleform GFX/CFX. Input ownership is never retained.</summary>
    public static ScaleformDocument Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 8)
            throw new InvalidDataException("The SWF/GFX header is truncated.");
        if (data.Length > MaximumFileLength)
            throw new InvalidDataException($"The file exceeds the {MaximumFileLength / 1024 / 1024} MiB safety limit.");
        string signature = Encoding.ASCII.GetString(data, 0, 3);
        if (signature == "ZWS")
            throw new NotSupportedException("LZMA-compressed ZWS movies are not supported. No data has been changed.");
        bool compressed = signature is "CWS" or "CFX";
        if (!compressed && signature is not ("FWS" or "GFX"))
            throw new NotSupportedException($"Unsupported movie signature '{signature}'. Expected FWS, CWS, GFX or CFX.");
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (declaredLength < 14 || declaredLength > MaximumFileLength)
            throw new InvalidDataException("The declared uncompressed movie length is invalid or exceeds the safety limit.");

        byte[] body;
        if (compressed)
        {
            body = Inflate(data, checked((int)declaredLength - 8));
        }
        else
        {
            if (declaredLength != data.Length)
                throw new InvalidDataException($"Movie length mismatch: header declares {declaredLength} bytes, file has {data.Length}.");
            body = data.AsSpan(8).ToArray();
        }
        return new ScaleformDocument((byte[])data.Clone(), body, signature, data[3]);
    }

    /// <summary>
    /// Serializes only supported changes, reparses the result, and verifies unchanged
    /// records before returning it. A no-change compile returns the original file bytes.
    /// </summary>
    public byte[] Compile()
    {
        if (!IsModified)
        {
            byte[] unchanged = (byte[])original.Clone();
            VerifyPreservation(Tags, Parse(unchanged).Tags);
            return unchanged;
        }
        using var body = new MemoryStream();
        byte[] header = (byte[])movieHeader.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(frameRateOffset), frameRate);
        body.Write(header);
        foreach (var tag in Tags)
            tag.WriteRecord(body);
        body.Write(tail);
        if (body.Length > MaximumFileLength - 8)
            throw new InvalidDataException("The compiled movie exceeds the safety limit.");

        using var output = new MemoryStream();
        output.Write(original, 0, 8);
        if (Signature is "CWS" or "CFX")
        {
            using var compressor = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
            body.Position = 0;
            body.CopyTo(compressor);
        }
        else
        {
            body.Position = 0;
            body.CopyTo(output);
        }
        byte[] result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)body.Length + 8));
        var reparsed = Parse(result);
        if (reparsed.frameRate != frameRate || reparsed.FrameCount != FrameCount ||
            reparsed.StageWidth != StageWidth || reparsed.StageHeight != StageHeight)
            throw new InvalidDataException("Compilation changed the movie header unexpectedly.");
        VerifyPreservation(Tags, reparsed.Tags);
        if (!tail.AsSpan().SequenceEqual(reparsed.tail))
            throw new InvalidDataException("Compilation changed the opaque movie trailer.");
        return result;
    }

    private static void VerifyPreservation(IReadOnlyList<ScaleformTag> before, IReadOnlyList<ScaleformTag> after)
    {
        if (before.Count != after.Count)
            throw new InvalidDataException("Compilation changed the tag count.");
        for (int i = 0; i < before.Count; i++)
        {
            if (before[i].Code != after[i].Code)
                throw new InvalidDataException("Compilation changed a tag type.");
            if (!before[i].IsModified && !before[i].OriginalRecord.Span.SequenceEqual(after[i].OriginalRecord.Span))
                throw new InvalidDataException($"Compilation changed an unedited {before[i].Name} record.");
            if (!before[i].Payload.Span.SequenceEqual(after[i].Payload.Span))
                throw new InvalidDataException($"Compilation did not preserve the intended {before[i].Name} payload.");
            VerifyPreservation(before[i].Children, after[i].Children);
        }
    }

    internal static IReadOnlyList<ScaleformTag> ParseTags(ReadOnlyMemory<byte> bytes, int absoluteOffset, byte version,
        int depth, ref int totalCount, out byte[] remaining)
    {
        if (depth > MaximumDepth)
            throw new InvalidDataException($"Sprite nesting exceeds the {MaximumDepth}-level safety limit.");
        var tags = new List<ScaleformTag>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (++totalCount > MaximumTagCount)
                throw new InvalidDataException("The movie has too many tag records.");
            int start = offset;
            if (bytes.Length - offset < 2)
                throw new InvalidDataException($"Truncated tag header at uncompressed offset {absoluteOffset + offset}.");
            ushort header = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Span[offset..]);
            ushort code = (ushort)(header >> 6);
            uint length = (uint)(header & 63);
            offset += 2;
            if (length == 63)
            {
                if (bytes.Length - offset < 4)
                    throw new InvalidDataException("Truncated long tag length.");
                length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span[offset..]);
                offset += 4;
            }
            if (length > bytes.Length - offset)
                throw new InvalidDataException($"{ScaleformTag.GetName(code)} extends beyond its container at uncompressed offset {absoluteOffset + start}.");
            if (code is 0 or 1 && length != 0)
                throw new InvalidDataException($"{ScaleformTag.GetName(code)} must have an empty payload.");
            int payloadLength = checked((int)length);
            var payload = bytes.Slice(offset, payloadLength);
            int headerLength = offset - start;
            var tag = new ScaleformTag(code, bytes.Slice(start, headerLength + payloadLength), payload,
                absoluteOffset + start, headerLength, version, depth, ref totalCount);
            tags.Add(tag);
            offset += payloadLength;
            if (code == 0)
            {
                remaining = bytes.Span[offset..].ToArray();
                return tags.AsReadOnly();
            }
        }
        throw new InvalidDataException("The movie or sprite is missing its terminating End tag.");
    }

    internal static int ReadRectangle(ReadOnlySpan<byte> bytes, int offset, out int xmin, out int xmax, out int ymin, out int ymax)
    {
        if ((uint)offset >= (uint)bytes.Length)
            throw new InvalidDataException("A SWF rectangle is truncated.");
        int bits = bytes[offset] >> 3;
        if (bits == 0)
            throw new InvalidDataException("A SWF rectangle must declare at least one coordinate bit.");
        int length = (5 + 4 * bits + 7) / 8;
        if (bytes.Length - offset < length)
            throw new InvalidDataException("A SWF rectangle is truncated.");
        int bitOffset = offset * 8 + 5;
        xmin = ReadSignedBits(bytes, ref bitOffset, bits);
        xmax = ReadSignedBits(bytes, ref bitOffset, bits);
        ymin = ReadSignedBits(bytes, ref bitOffset, bits);
        ymax = ReadSignedBits(bytes, ref bitOffset, bits);
        return length;
    }

    private static int ReadSignedBits(ReadOnlySpan<byte> bytes, ref int offset, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++, offset++)
            value = (value << 1) | ((bytes[offset / 8] >> (7 - offset % 8)) & 1);
        return (value << (32 - count)) >> (32 - count);
    }

    private static byte[] Inflate(byte[] data, int expectedLength)
    {
        // RFC 1950 wrapper: CMF, FLG, DEFLATE, Adler-32. Check it independently
        // because decompression APIs may accept a stream with a missing zlib trailer.
        if (data.Length < 14 || (data[8] & 15) != 8 || data[8] >> 4 > 7 ||
            ((data[8] << 8) | data[9]) % 31 != 0 || (data[9] & 32) != 0)
            throw new InvalidDataException("The movie's zlib header is invalid or uses an unsupported preset dictionary.");
        using var source = new MemoryStream(data, 8, data.Length - 8, writable: false);
        using var inflater = new ZLibStream(source, CompressionMode.Decompress);
        byte[] result = new byte[expectedLength];
        try
        {
            int read = 0;
            while (read < result.Length)
            {
                int n = inflater.Read(result, read, result.Length - read);
                if (n == 0)
                    throw new InvalidDataException("The compressed movie ended before its declared uncompressed length.");
                read += n;
            }
            if (inflater.ReadByte() != -1)
                throw new InvalidDataException("The compressed movie exceeds its declared uncompressed length.");
        }
        catch (IOException e)
        {
            throw new InvalidDataException("The movie's DEFLATE stream is invalid.", e);
        }
        uint a = 1, b = 0;
        foreach (byte value in result)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        uint storedChecksum = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(data.Length - 4));
        if (storedChecksum != (b << 16 | a))
            throw new InvalidDataException("The compressed movie's Adler-32 checksum does not match.");
        return result;
    }
}
