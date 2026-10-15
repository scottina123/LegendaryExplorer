using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using LegendaryExplorerCore.Packages;
using NAudio.Wave;

namespace LegendaryExplorer.Audio;

/// <summary>Produces the game's Wwise Vorbis media using only the codecs bundled with LEX.</summary>
public static class WwiseAudioEncoder
{
    public static void ConvertAudioFile(MEGame game, string inputPath, string outputPath, int sampleRate = 24000)
    {
        ValidateGame(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!AudioInputConverter.IsSupportedAudioFile(inputPath))
            throw new NotSupportedException("Only WAV and MP3 files can be converted to game audio.");
        if (Path.GetFullPath(inputPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The converted audio must have a different path from its source.", nameof(outputPath));
        if (sampleRate == 0)
        {
            using WaveStream reader = Path.GetExtension(inputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? new WaveFileReader(inputPath) : new AudioFileReader(inputPath);
            sampleRate = reader.WaveFormat.SampleRate;
        }
        if (sampleRate < 8000 || sampleRate > 192000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Game audio sample rates must be between 8000 and 192000 Hz.");

        NativeVorbisEncoder.Result encoded = NativeVorbisEncoder.Encode(inputPath, sampleRate);
        byte[] media = BuildMedia(encoded, game);
        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        // A failed write cannot leave a truncated media file that an importer might consume.
        string temporary = Path.Combine(directory, $".LEX_Audio_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, media);
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void ValidateGame(MEGame game)
    {
        if (game is not (MEGame.ME3 or MEGame.LE2 or MEGame.LE3))
            throw new NotSupportedException($"Game audio conversion is not supported for {game}.");
    }

    private static byte[] BuildMedia(NativeVorbisEncoder.Result encoded, MEGame game)
    {
        byte[] id = encoded.Identification;
        int channels = id[11];
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(id.AsSpan(12));
        int smallExponent = id[28] & 15, largeExponent = id[28] >> 4;
        byte[] setup = WwiseVorbisSetup.CompactSetup(encoded.Setup, channels, game != MEGame.ME3, out bool[] modes);
        int modeBits = BitOperations.Log2((uint)modes.Length - 1) + 1;
        if (modes.Length == 1) modeBits = 0;
        using var audio = new MemoryStream();
        using var seek = new MemoryStream();
        using var audioWriter = new BinaryWriter(audio, Encoding.ASCII, leaveOpen: true);
        using var seekWriter = new BinaryWriter(seek, Encoding.ASCII, leaveOpen: true);
        int setupSize = checked(setup.Length + 2), maxPacket = 0;
        long decodedFrames = 0, previousSeekFrame = 0, previousSeekOffset = 0;
        int previousBlock = 0;
        foreach (NativeVorbisEncoder.Packet packet in encoded.Packets)
        {
            if (previousBlock != 0) decodedFrames += (previousBlock + packet.BlockSize) / 4;
            previousBlock = packet.BlockSize;
            long offset = setupSize + audio.Position;
            // Wwise's seek entries are relative 16-bit frame and byte offsets, based at the setup.
            if (decodedFrames - previousSeekFrame >= 16384)
            {
                seekWriter.Write(checked((ushort)(decodedFrames - previousSeekFrame)));
                seekWriter.Write(checked((ushort)(offset - previousSeekOffset)));
                previousSeekFrame = decodedFrames;
                previousSeekOffset = offset;
            }
            byte[] payload = CompactPacket(packet.Data, modes, modeBits, smallExponent != largeExponent);
            maxPacket = Math.Max(maxPacket, payload.Length);
            audioWriter.Write(checked((ushort)payload.Length));
            audioWriter.Write(payload);
        }
        int extraFrames = checked((int)(decodedFrames - encoded.SampleCount));
        if (extraFrames < 0 || extraFrames > ushort.MaxValue)
            throw new InvalidDataException("The encoder returned an inconsistent audio sample count.");
        int seekSize = checked((int)seek.Length);
        int dataSize = checked(seekSize + setupSize + (int)audio.Length);

        // Wwise format fields (offsets relative to the 66-byte fmt chunk).
        // Format reference: https://github.com/vgmstream/vgmstream/blob/master/src/meta/wwise.c
        byte[] format = new byte[66];
        Put16(format, 0, ushort.MaxValue);
        Put16(format, 2, channels);
        Put32(format, 4, (uint)sampleRate);
        Put32(format, 8, (uint)Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(id.AsSpan(20)) / 8));
        Put16(format, 16, 48);
        Put32(format, 20, game == MEGame.ME3 ? encoded.ChannelMask :
            encoded.ChannelMask << 12 | 0x100u | (uint)channels);
        Put32(format, 24, (uint)encoded.SampleCount);
        Put32(format, 28, (uint)setupSize);
        Put32(format, 32, (uint)(dataSize - seekSize));
        Put16(format, 38, extraFrames);
        Put32(format, 40, (uint)seekSize);
        Put32(format, 44, (uint)(seekSize + setupSize));
        Put16(format, 48, maxPacket);
        Put16(format, 50, extraFrames);
        // Reserve a conservative setup arena for all supported encoder modes. Wwise uses
        // these fields as allocation budgets; underestimating them can break game playback.
        Put32(format, 52, 512 * 1024);
        Put32(format, 56, 512 * 1024);
        // Wwise's cache key is FNV-1 over the reduced setup payload, without its
        // length prefix. The reference bank's two setups reproduce 0xB3DEA448.
        uint setupHash = 2166136261;
        foreach (byte value in setup) setupHash = unchecked((setupHash * 16777619) ^ value);
        Put32(format, 60, setupHash);
        format[64] = (byte)smallExponent;
        format[65] = (byte)largeExponent;

        using var result = new MemoryStream();
        using var writer = new BinaryWriter(result, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(0); // patched after serializing chunks
        writer.Write("WAVE"u8);
        if (game == MEGame.ME3)
        {
            // ME3's import step merges the old authoring fmt/vorb chunks into its runtime fmt.
            byte[] oldFormat = format[..24];
            Put16(oldFormat, 16, 6);
            WriteChunk(writer, "fmt "u8, oldFormat);
            WriteChunk(writer, "vorb"u8, format.AsSpan(24));
        }
        else WriteChunk(writer, "fmt "u8, format);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(seek.ToArray());
        writer.Write(checked((ushort)setup.Length));
        writer.Write(setup);
        writer.Write(audio.ToArray());
        // Wwise media does not align the final data chunk. Its runtime rejects a
        // trailing RIFF padding byte even though general-purpose decoders accept it.
        result.Position = 4;
        writer.Write(checked((int)result.Length - 8));
        return result.ToArray();
    }

    private static byte[] CompactPacket(byte[] packet, bool[] modes, int modeBits, bool modified)
    {
        if (packet.Length == 0 || (packet[0] & 1) != 0)
            throw new InvalidDataException("The encoder returned an invalid Vorbis audio packet.");
        if (!modified) return packet;
        int mode = (packet[0] >> 1) & ((1 << modeBits) - 1);
        if (mode >= modes.Length) throw new InvalidDataException("Invalid Vorbis packet mode.");
        int skip = modes[mode] ? 3 : 1; // packet type plus the two long-window flags
        int bits = packet.Length * 8 - skip;
        byte[] reduced = new byte[(bits + 7) / 8];
        // Wwise infers window flags from adjacent modes, so retain just the mode and payload.
        for (int outputBit = 0; outputBit < bits; outputBit++)
        {
            int inputBit = outputBit < modeBits ? outputBit + 1 : outputBit + skip;
            reduced[outputBit >> 3] |= (byte)(((packet[inputBit >> 3] >> (inputBit & 7)) & 1) << (outputBit & 7));
        }
        return reduced;
    }

    private static void Put16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), checked((ushort)value));
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void WriteChunk(BinaryWriter writer, ReadOnlySpan<byte> name, ReadOnlySpan<byte> contents)
    {
        writer.Write(name);
        writer.Write(contents.Length);
        writer.Write(contents);
        if ((contents.Length & 1) != 0) writer.Write((byte)0);
    }
}
