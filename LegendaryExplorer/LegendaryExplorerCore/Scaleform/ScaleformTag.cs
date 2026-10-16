using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LegendaryExplorerCore.Scaleform;

/// <summary>A SWF record whose opaque bytes remain authoritative until explicitly edited.</summary>
public sealed class ScaleformTag
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlyMemory<byte> originalPayload;
    private readonly int originalHeaderLength;
    private readonly byte version;
    private readonly byte[] childTail = Array.Empty<byte>();
    private readonly List<ScaleformTag> childTags;
    private bool childrenModified;
    private byte[] replacement;
    private int textOffset = -1;
    private int textByteLength;
    private bool hasInitialText;
    private int editTextFlagsOffset;

    public ushort Code { get; }
    public string Name => GetName(Code);
    public string Category => Code switch
    {
        0 or 1 or 4 or 5 or 26 or 28 or 39 or 43 or 70 or 86 => "Timeline",
        12 or 59 or 72 or 82 => "Scripts",
        10 or 11 or 13 or 33 or 37 or 48 or 62 or 73 or 74 or 75 or 88 or 91 or 1002 or 1005 => "Text and fonts",
        6 or 8 or 20 or 21 or 35 or 36 or 90 or 1001 or 1003 or 1004 or 1008 or 1009 => "Images",
        2 or 22 or 32 or 46 or 83 or 84 => "Shapes",
        14 or 15 or 18 or 19 or 45 or 89 or 1006 or 1007 => "Audio",
        7 or 17 or 23 or 34 => "Buttons",
        60 or 61 => "Video",
        9 or 24 or 41 or 56 or 57 or 58 or 64 or 65 or 66 or 69 or 71 or 76 or 77 or 78 or 87 or 93 => "Movie data",
        >= 1000 => "Scaleform extensions",
        _ => "Unknown"
    };
    /// <summary>The current payload, including compiled child records for an edited sprite.</summary>
    public ReadOnlyMemory<byte> Payload => Code == 39 && IsModified
        ? BuildSpritePayload()
        : replacement == null ? originalPayload : replacement;
    /// <summary>Offset in the uncompressed file, including the eight-byte file header.</summary>
    public int OriginalOffset { get; }
    public IReadOnlyList<ScaleformTag> Children { get; } = Array.Empty<ScaleformTag>();
    public bool IsModified => replacement != null || childrenModified || Children.Any(c => c.IsModified);
    public bool CanEditText => textOffset >= 0;
    public string TextValue
    {
        get
        {
            if (!CanEditText || (Code == 37 && !hasInitialText && replacement == null))
                return string.Empty;
            var payload = Payload.Span;
            int end = payload[textOffset..].IndexOf((byte)0);
            return DecodeText(payload.Slice(textOffset, end));
        }
    }
    internal ReadOnlyMemory<byte> OriginalRecord { get; }

    internal ScaleformTag(ushort code, ReadOnlyMemory<byte> record, ReadOnlyMemory<byte> payload, int offset,
        int headerLength, byte version, int depth, ref int count)
    {
        Code = code;
        OriginalRecord = record;
        originalPayload = payload;
        OriginalOffset = offset;
        originalHeaderLength = headerLength;
        this.version = version;
        if (code == 39)
        {
            if (payload.Length < 4)
                throw new InvalidDataException("DefineSprite is missing its character ID or frame count.");
            childTags = ScaleformDocument.ParseTags(payload[4..], offset + headerLength + 4, version, depth + 1, ref count, out childTail).ToList();
            Children = childTags.AsReadOnly();
        }
        if (code == 9 && payload.Length != 3)
            throw new InvalidDataException("SetBackgroundColor must contain exactly three RGB bytes.");
        if (code is 43 or 77 or 37)
            LocateEditableText();
    }

    /// <summary>Replaces a string without modifying its surrounding flags, IDs, styles or references.</summary>
    public void SetText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!CanEditText)
            throw new InvalidOperationException($"{Name} does not contain a supported editable string.");
        if (value.Contains('\0'))
            throw new ArgumentException("SWF strings cannot contain a null character.", nameof(value));
        byte[] encoded;
        if (version < 6)
        {
            if (value.Any(c => c > 127))
                throw new NotSupportedException("Non-ASCII edits in pre-SWF6 files require a code-page choice and are not supported.");
            encoded = Encoding.ASCII.GetBytes(value);
        }
        else
        {
            encoded = StrictUtf8.GetBytes(value);
        }
        // Always build from the original payload, so repeated edits and reverting are stable.
        byte[] edited = new byte[checked(originalPayload.Length - textByteLength + encoded.Length + (hasInitialText ? 0 : Code == 37 ? 1 : 0))];
        originalPayload.Span[..textOffset].CopyTo(edited);
        encoded.CopyTo(edited, textOffset);
        edited[textOffset + encoded.Length] = 0;
        int oldEnd = textOffset + textByteLength + (Code == 37 && !hasInitialText ? 0 : 1);
        originalPayload.Span[oldEnd..].CopyTo(edited.AsSpan(textOffset + encoded.Length + 1));
        if (Code == 37)
            edited[editTextFlagsOffset] |= 0x80; // HasText is the first high-order flag bit.
        replacement = edited.AsSpan().SequenceEqual(originalPayload.Span) ? null : edited;
    }

    public void SetBackgroundColor(byte r, byte g, byte b)
    {
        if (Code != 9)
            throw new InvalidOperationException("Only SetBackgroundColor records contain an editable movie background.");
        byte[] color = { r, g, b };
        replacement = color.AsSpan().SequenceEqual(originalPayload.Span) ? null : color;
    }

    /// <summary>Changes only a placement's transform, retaining its character, effects and scripts verbatim.</summary>
    public void SetPlacementMatrix(ScaleformMatrix matrix)
    {
        byte[] edited = ScaleformPlacement.WithMatrix(this, originalPayload.Span, matrix);
        replacement = edited.AsSpan().SequenceEqual(originalPayload.Span) ? null : edited;
    }

    internal void InsertTimelineRecord(int index, ScaleformTag tag)
    {
        if (Code != 39) throw new InvalidOperationException("Only a movie clip has a child timeline.");
        childTags.Insert(index, tag);
        childrenModified = true;
    }

    /// <summary>
    /// Replaces a DoAction/DoInitAction AVM1 action stream. For DoInitAction the
    /// supplied stream excludes the two-byte SpriteID, which is preserved.
    /// </summary>
    public void SetActionBytes(byte[] actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (Code is not (12 or 59))
            throw new InvalidOperationException("Only DoAction and DoInitAction AVM1 records can be replaced here.");
        ScaleformActionScript.Validate(actions);
        int prefixLength = Code == 59 ? 2 : 0;
        if (originalPayload.Length < prefixLength)
            throw new InvalidDataException("DoInitAction is missing its SpriteID.");
        byte[] edited = new byte[checked(prefixLength + actions.Length)];
        originalPayload.Span[..prefixLength].CopyTo(edited);
        actions.CopyTo(edited, prefixLength);
        replacement = edited.AsSpan().SequenceEqual(originalPayload.Span) ? null : edited;
    }

    private byte[] BuildSpritePayload()
    {
        using var sprite = new MemoryStream();
        sprite.Write(originalPayload.Span[..4]);
        foreach (var child in Children)
            child.WriteRecord(sprite);
        sprite.Write(childTail);
        return sprite.ToArray();
    }

    internal void WriteRecord(Stream output)
    {
        if (!IsModified)
        {
            output.Write(OriginalRecord.Span);
            return;
        }
        ReadOnlyMemory<byte> payload = Payload;
        bool longHeader = originalHeaderLength == 6 || payload.Length >= 63;
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)((Code << 6) | (longHeader ? 63 : payload.Length)));
        if (longHeader)
            BinaryPrimitives.WriteUInt32LittleEndian(header[2..], checked((uint)payload.Length));
        output.Write(header[..(longHeader ? 6 : 2)]);
        output.Write(payload.Span);
    }

    private void LocateEditableText()
    {
        var payload = originalPayload.Span;
        int offset = 0;
        try
        {
            if (Code == 37)
            {
                Require(payload, 0, 2);
                offset = 2 + ScaleformDocument.ReadRectangle(payload, 2, out _, out _, out _, out _);
                Require(payload, offset, 2);
                editTextFlagsOffset = offset;
                byte flags1 = payload[offset++], flags2 = payload[offset++];
                hasInitialText = (flags1 & 0x80) != 0;
                bool hasFont = (flags1 & 1) != 0;
                // SWF9 FontClass layouts are outside this AVM1 editor's verified corpus.
                // Keep them opaque rather than risk mistaking FontHeight for VariableName.
                if ((flags2 & 0x80) != 0) return;
                if (hasFont) { Require(payload, offset, 2); offset += 2; }
                if (hasFont) { Require(payload, offset, 2); offset += 2; } // FontHeight
                if ((flags1 & 4) != 0) { Require(payload, offset, 4); offset += 4; } // RGBA
                if ((flags1 & 2) != 0) { Require(payload, offset, 2); offset += 2; } // MaxLength
                if ((flags2 & 0x20) != 0) { Require(payload, offset, 9); offset += 9; } // layout
                offset = EndOfString(payload, offset) + 1; // VariableName
                if (!hasInitialText)
                {
                    // Unknown trailing data cannot safely be interpreted as absent InitialText.
                    if (offset == payload.Length)
                    {
                        textOffset = offset;
                        textByteLength = 0;
                    }
                    return;
                }
            }
            else
            {
                hasInitialText = true;
            }
            int end = EndOfString(payload, offset);
            _ = DecodeText(payload.Slice(offset, end - offset));
            textOffset = offset;
            textByteLength = end - offset;
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or NotSupportedException)
        {
            // Unsupported or malformed string layouts remain opaque and survive unchanged.
            textOffset = -1;
        }
    }

    private string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (version >= 6)
            return StrictUtf8.GetString(bytes);
        foreach (byte value in bytes)
            if (value > 127)
                throw new NotSupportedException("The pre-SWF6 string encoding is not known.");
        return Encoding.ASCII.GetString(bytes);
    }

    private static int EndOfString(ReadOnlySpan<byte> payload, int offset)
    {
        Require(payload, offset, 1);
        int length = payload[offset..].IndexOf((byte)0);
        if (length < 0)
            throw new InvalidDataException("A SWF string is not null terminated.");
        return offset + length;
    }

    private static void Require(ReadOnlySpan<byte> payload, int offset, int count)
    {
        if (offset < 0 || count < 0 || payload.Length - offset < count)
            throw new InvalidDataException("The text field record is truncated.");
    }

    internal static string GetName(ushort code) => code switch
    {
        0 => "End", 1 => "ShowFrame", 2 => "DefineShape", 4 => "PlaceObject", 5 => "RemoveObject",
        6 => "DefineBits", 7 => "DefineButton", 8 => "JPEGTables", 9 => "SetBackgroundColor",
        10 => "DefineFont", 11 => "DefineText", 12 => "DoAction", 13 => "DefineFontInfo",
        14 => "DefineSound", 15 => "StartSound", 17 => "DefineButtonSound", 18 => "SoundStreamHead",
        19 => "SoundStreamBlock", 20 => "DefineBitsLossless", 21 => "DefineBitsJPEG2", 22 => "DefineShape2",
        23 => "DefineButtonCxform", 24 => "Protect", 26 => "PlaceObject2", 28 => "RemoveObject2",
        32 => "DefineShape3", 33 => "DefineText2", 34 => "DefineButton2", 35 => "DefineBitsJPEG3",
        36 => "DefineBitsLossless2", 37 => "DefineEditText", 39 => "DefineSprite", 41 => "ProductInfo",
        43 => "FrameLabel", 45 => "SoundStreamHead2", 46 => "DefineMorphShape", 48 => "DefineFont2",
        56 => "ExportAssets", 57 => "ImportAssets", 58 => "EnableDebugger", 59 => "DoInitAction",
        60 => "DefineVideoStream", 61 => "VideoFrame", 62 => "DefineFontInfo2", 64 => "EnableDebugger2",
        65 => "ScriptLimits", 66 => "SetTabIndex", 69 => "FileAttributes", 70 => "PlaceObject3",
        71 => "ImportAssets2", 72 => "DoABC (legacy)", 73 => "DefineFontAlignZones", 74 => "CSMTextSettings",
        75 => "DefineFont3", 76 => "SymbolClass", 77 => "Metadata", 78 => "DefineScalingGrid",
        82 => "DoABC", 83 => "DefineShape4", 84 => "DefineMorphShape2", 86 => "DefineSceneAndFrameLabelData",
        87 => "DefineBinaryData", 88 => "DefineFontName", 89 => "StartSound2", 90 => "DefineBitsJPEG4",
        91 => "DefineFont4", 93 => "EnableTelemetry", 1000 => "ExporterInfo", 1001 => "DefineExternalImage",
        1002 => "FontTextureInfo", 1003 => "DefineExternalGradientImage", 1004 => "DefineGradientMap",
        1005 => "DefineCompactedFont", 1006 => "DefineExternalSound", 1007 => "DefineExternalStreamSound",
        1008 => "DefineSubImage", 1009 => "DefineExternalImage2", _ => $"Tag {code}"
    };
}
