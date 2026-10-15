using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LegendaryExplorer.Audio;

/// <summary>Encodes with the Xiph libraries distributed with LEX, without an authoring application.</summary>
internal static unsafe class NativeVorbisEncoder
{
    internal sealed record Packet(byte[] Data, int BlockSize, long GranulePosition);
    internal sealed record Result(byte[] Identification, byte[] Setup, List<Packet> Packets, int SampleCount, uint ChannelMask);

    internal static Result Encode(string inputPath, int sampleRate)
    {
        using var reader = OpenReader(inputPath);
        int channels = reader.WaveFormat.Channels;
        if (channels is < 1 or > 8)
            throw new NotSupportedException("Audio import supports between one and eight audio channels.");
        uint channelMask = GetChannelMask(inputPath, channels);
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.SampleRate != sampleRate)
            source = new WdlResamplingSampleProvider(source, sampleRate);

        Info info = default;
        Comment comment = default;
        Dsp dsp = default;
        Block block = default;
        bool dspInitialized = false, blockInitialized = false;
        vorbis_info_init(&info);
        vorbis_comment_init(&comment);
        try
        {
            // Quality 0.4 uses codebooks present in both games' bundled codebook tables,
            // including at the lower sample rates used for dialogue.
            if (channels <= 2)
            {
                Check(vorbis_encode_init_vbr(&info, channels, sampleRate, 0.4f));
            }
            else
            {
                Check(vorbis_encode_setup_vbr(&info, channels, sampleRate, 0.4f));
                // Wwise uses WAV speaker order. Uncoupled coding preserves that order
                // without assuming Vorbis's different 5.1/LFE channel positions, and
                // avoids surround-specific codebooks unavailable in the games.
                int coupling = 0;
                Check(vorbis_encode_ctl(&info, 0x41, &coupling)); // OV_ECTL_COUPLING_SET
                Check(vorbis_encode_setup_init(&info));
            }
            Check(vorbis_analysis_init(&dsp, &info));
            dspInitialized = true;
            Check(vorbis_block_init(&dsp, &block));
            blockInitialized = true;
            OggPacket identification = default, comments = default, setup = default;
            Check(vorbis_analysis_headerout(&dsp, &comment, &identification, &comments, &setup));
            byte[] identificationData = CopyPacket(identification);
            byte[] setupData = CopyPacket(setup);
            var packets = new List<Packet>();
            var samples = new float[4096 * channels];
            long sampleCount = 0;
            int read;
            do
            {
                read = source.Read(samples, 0, samples.Length);
                if (read % channels != 0)
                    throw new InvalidDataException("The input contains an incomplete audio frame.");
                int frames = read / channels;
                sampleCount += frames;
                if (sampleCount > int.MaxValue)
                    throw new NotSupportedException("The audio is too long for a game audio stream.");
                if (frames > 0)
                {
                    float** buffer = vorbis_analysis_buffer(&dsp, frames);
                    if (buffer == null)
                        throw new InvalidOperationException("The audio encoder could not allocate its sample buffer.");
                    for (int channel = 0; channel < channels; channel++)
                        for (int frame = 0; frame < frames; frame++)
                            buffer[channel][frame] = samples[frame * channels + channel];
                }
                Check(vorbis_analysis_wrote(&dsp, frames));
                while (vorbis_analysis_blockout(&dsp, &block) == 1)
                {
                    Check(vorbis_analysis(&block, null));
                    Check(vorbis_bitrate_addblock(&block));
                    OggPacket packet = default;
                    while (vorbis_bitrate_flushpacket(&dsp, &packet) == 1)
                    {
                        int blockSize = vorbis_packet_blocksize(&info, &packet);
                        Check(blockSize);
                        packets.Add(new Packet(CopyPacket(packet), blockSize, packet.GranulePosition));
                    }
                }
            } while (read > 0);
            if (sampleCount == 0 || packets.Count == 0)
                throw new InvalidDataException("The selected audio file contains no audio samples.");
            return new Result(identificationData, setupData, packets, (int)sampleCount, channelMask);
        }
        finally
        {
            if (blockInitialized) vorbis_block_clear(&block);
            if (dspInitialized) vorbis_dsp_clear(&dsp);
            vorbis_comment_clear(&comment);
            vorbis_info_clear(&info);
        }
    }

    private static WaveStream OpenReader(string inputPath)
    {
        if (!Path.GetExtension(inputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return new AudioFileReader(inputPath);

        var wave = new WaveFileReader(inputPath);
        try
        {
            if (wave.WaveFormat.Encoding == WaveFormatEncoding.Extensible)
            {
                // AudioFileReader sends extensible PCM through ACM, which cannot
                // handle common surround files. Reinterpret its unchanged sample
                // bytes using the PCM/float subformat instead of invoking a codec.
                byte[] format = SerializeFormat(wave.WaveFormat);
                if (format.Length < 44)
                    throw new InvalidDataException("The extensible WAV format is truncated.");
                Guid subformat = new(format.AsSpan(28, 16));
                var pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
                var floatingPoint = new Guid("00000003-0000-0010-8000-00aa00389b71");
                WaveFormat standard;
                if (subformat == pcm)
                    standard = new WaveFormat(wave.WaveFormat.SampleRate, wave.WaveFormat.BitsPerSample, wave.WaveFormat.Channels);
                else if (subformat == floatingPoint)
                    standard = WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat,
                        wave.WaveFormat.SampleRate, wave.WaveFormat.Channels, wave.WaveFormat.AverageBytesPerSecond,
                        wave.WaveFormat.BlockAlign, wave.WaveFormat.BitsPerSample);
                else
                    throw new NotSupportedException("The extensible WAV must contain PCM or floating-point audio.");
                return new OwnedRawWaveStream(wave, standard);
            }
            if (wave.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)
                return wave;
            return WaveFormatConversionStream.CreatePcmStream(wave);
        }
        catch
        {
            wave.Dispose();
            throw;
        }
    }

    // NAudio's RawSourceWaveStream leaves its source open when disposed.
    private sealed class OwnedRawWaveStream(WaveStream source, WaveFormat format) : RawSourceWaveStream(source, format)
    {
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) source.Dispose(); }
        }
    }

    private static byte[] SerializeFormat(WaveFormat waveFormat)
    {
        using var format = new MemoryStream();
        using (var writer = new BinaryWriter(format, Encoding.ASCII, leaveOpen: true))
            waveFormat.Serialize(writer);
        return format.ToArray();
    }

    private static uint GetChannelMask(string inputPath, int channels)
    {
        if (Path.GetExtension(inputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            using var wave = new WaveFileReader(inputPath);
            if (wave.WaveFormat.Encoding == WaveFormatEncoding.Extensible)
            {
                // NAudio exposes the extensible channel mask through serialization.
                // Serialize includes a four-byte format length before WAVEFORMATEXTENSIBLE.
                byte[] format = SerializeFormat(wave.WaveFormat);
                if (format.Length < 28)
                    throw new InvalidDataException("The WAV channel configuration is truncated.");
                uint mask = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(24));
                if (mask != 0)
                {
                    if (mask > 0x7FFFF || BitOperations.PopCount(mask) != channels)
                        throw new NotSupportedException("The WAV speaker mask does not match its audio channels or exceeds the game's speaker configuration.");
                    return mask;
                }
            }
        }
        // Wwise's default layouts, in ascending Microsoft speaker-bit order.
        return channels switch
        {
            1 => 0x4,   // C
            2 => 0x3,   // L R
            3 => 0x7,   // L R C
            4 => 0x33,  // L R BL BR
            5 => 0x37,  // L R C BL BR
            6 => 0x3F,  // L R C LFE BL BR
            7 => 0x637, // L R C BL BR SL SR
            8 => 0x63F, // L R C LFE BL BR SL SR
            _ => throw new ArgumentOutOfRangeException(nameof(channels))
        };
    }

    private static byte[] CopyPacket(OggPacket packet)
    {
        if (packet.Size <= 0 || packet.Data == IntPtr.Zero)
            throw new InvalidDataException("The audio encoder returned an empty packet.");
        byte[] bytes = new byte[packet.Size];
        Marshal.Copy(packet.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void Check(int result)
    {
        if (result < 0) throw new InvalidOperationException($"Vorbis encoding failed (error {result}).");
    }

    // Windows libvorbis ABI (C long is 32 bits, including on x64).
    // Field layouts: https://github.com/xiph/vorbis/blob/master/include/vorbis/codec.h
    // and https://github.com/xiph/ogg/blob/master/include/ogg/ogg.h
    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public int Version, Channels, Rate, BitrateUpper, BitrateNominal, BitrateLower, BitrateWindow;
        public IntPtr CodecSetup;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Comment
    {
        public IntPtr UserComments, CommentLengths;
        public int Comments;
        public IntPtr Vendor;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Dsp
    {
        public int Analysis;
        public IntPtr Info, Pcm, PcmReturned;
        public int PcmStorage, PcmCurrent, PcmRead, PreExtrapolate, EndOfStream;
        public int PreviousWindow, Window, NextWindow, CenterWindow;
        public long GranulePosition, Sequence, GlueBits, TimeBits, FloorBits, ResidueBits;
        public IntPtr BackendState;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PackBuffer
    {
        public int EndByte, EndBit;
        public IntPtr Buffer, Pointer;
        public int Storage;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Block
    {
        public IntPtr Pcm;
        public PackBuffer PackBuffer;
        public int PreviousWindow, Window, NextWindow, PcmEnd, Mode, EndOfStream;
        public long GranulePosition, Sequence;
        public IntPtr Dsp, LocalStore;
        public int LocalTop, LocalAllocation, TotalUse;
        public IntPtr Reap;
        public int GlueBits, TimeBits, FloorBits, ResidueBits;
        public IntPtr Internal;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct OggPacket
    {
        public IntPtr Data;
        public int Size, BeginningOfStream, EndOfStream;
        public long GranulePosition, PacketNumber;
    }

    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vorbis_info_init(Info* info);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vorbis_info_clear(Info* info);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vorbis_comment_init(Comment* comment);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vorbis_comment_clear(Comment* comment);
    [DllImport("vorbisenc.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_encode_init_vbr(Info* info, int channels, int rate, float quality);
    [DllImport("vorbisenc.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_encode_setup_vbr(Info* info, int channels, int rate, float quality);
    [DllImport("vorbisenc.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_encode_ctl(Info* info, int request, void* argument);
    [DllImport("vorbisenc.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_encode_setup_init(Info* info);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_analysis_init(Dsp* dsp, Info* info);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vorbis_dsp_clear(Dsp* dsp);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_block_init(Dsp* dsp, Block* block);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_block_clear(Block* block);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_analysis_headerout(Dsp* dsp, Comment* comment, OggPacket* identification, OggPacket* comments, OggPacket* setup);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern float** vorbis_analysis_buffer(Dsp* dsp, int samples);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_analysis_wrote(Dsp* dsp, int samples);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_analysis_blockout(Dsp* dsp, Block* block);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_analysis(Block* block, OggPacket* packet);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_bitrate_addblock(Block* block);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_bitrate_flushpacket(Dsp* dsp, OggPacket* packet);
    [DllImport("vorbis.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int vorbis_packet_blocksize(Info* info, OggPacket* packet);
}
