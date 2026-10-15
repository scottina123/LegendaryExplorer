using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using LegendaryExplorer.Misc;

namespace LegendaryExplorer.Audio
{
    /// <summary>
    /// Converts the standard Vorbis setup emitted by the bundled encoder into Wwise's
    /// reduced setup. Codebooks are compared by their decoded contents, since the same
    /// Huffman book can have different ordered/sparse representations.
    /// </summary>
    internal static class WwiseVorbisSetup
    {
        private static readonly Lazy<Dictionary<string, int>> OriginalCodebooks = new(() => LoadCodebooks("packed_codebooks.bin"));
        private static readonly Lazy<Dictionary<string, int>> LegendaryCodebooks = new(() => LoadCodebooks("packed_codebooks_aoTuV_603.bin"));

        public static byte[] CompactSetup(byte[] standardSetup, int channels, bool legendary, out bool[] modeBlockFlags)
        {
            ArgumentNullException.ThrowIfNull(standardSetup);
            if (channels < 1 || channels > 8)
                throw new ArgumentOutOfRangeException(nameof(channels));
            if (standardSetup.Length < 8 || standardSetup[0] != 5 ||
                Encoding.ASCII.GetString(standardSetup, 1, 6) != "vorbis")
                throw new InvalidDataException("The encoder did not return a Vorbis setup packet.");

            var input = new BitReader(standardSetup, 7 * 8);
            var output = new BitWriter();
            var codebooks = (legendary ? LegendaryCodebooks : OriginalCodebooks).Value;
            int bookCount = (int)Transfer(input, output, 8) + 1;
            for (int i = 0; i < bookCount; i++)
            {
                string signature = ReadCodebook(input, false);
                if (!codebooks.TryGetValue(signature, out int id))
                    throw new NotSupportedException("The bundled Vorbis encoder selected a codebook unavailable in the game's decoder. Use a supported sample rate and encoding quality.");
                output.Write((uint)id, 10);
            }

            // Vorbis time-domain transforms are reserved and omitted by Wwise.
            int timeCount = (int)input.Read(6) + 1;
            for (int i = 0; i < timeCount; i++)
                Require(input.Read(16) == 0, "Unsupported Vorbis time-domain transform.");

            int floorCount = (int)Transfer(input, output, 6) + 1;
            for (int i = 0; i < floorCount; i++)
            {
                Require(input.Read(16) == 1, "Wwise requires Vorbis floor type 1.");
                int partitions = (int)Transfer(input, output, 5);
                var partitionClasses = new int[partitions];
                int maximumClass = -1;
                for (int j = 0; j < partitions; j++)
                {
                    partitionClasses[j] = (int)Transfer(input, output, 4);
                    maximumClass = Math.Max(maximumClass, partitionClasses[j]);
                }

                var dimensions = new int[maximumClass + 1];
                for (int j = 0; j <= maximumClass; j++)
                {
                    dimensions[j] = (int)Transfer(input, output, 3) + 1;
                    int subclasses = (int)Transfer(input, output, 2);
                    if (subclasses != 0)
                        Require(Transfer(input, output, 8) < bookCount, "Invalid Vorbis floor masterbook.");
                    for (int k = 0; k < 1 << subclasses; k++)
                        Require(Transfer(input, output, 8) <= bookCount, "Invalid Vorbis floor subclass book.");
                }
                if (partitions == 0)
                {
                    // Wwise retains an unused class zero even for a floor with no partitions.
                    output.Write(0, 3);
                    output.Write(0, 2);
                    output.Write(0, 8);
                }
                Transfer(input, output, 2); // floor multiplier
                int rangeBits = (int)Transfer(input, output, 4);
                foreach (int partitionClass in partitionClasses)
                    for (int j = 0; j < dimensions[partitionClass]; j++)
                        Transfer(input, output, rangeBits);
            }

            int residueCount = (int)Transfer(input, output, 6) + 1;
            for (int i = 0; i < residueCount; i++)
            {
                uint residueType = input.Read(16);
                Require(residueType <= 2, "Unsupported Vorbis residue type.");
                output.Write(residueType, 2);
                Transfer(input, output, 24); // begin
                Transfer(input, output, 24); // end
                Transfer(input, output, 24); // partition size
                int classifications = (int)Transfer(input, output, 6) + 1;
                Require(Transfer(input, output, 8) < bookCount, "Invalid Vorbis residue classbook.");
                var cascades = new uint[classifications];
                for (int j = 0; j < classifications; j++)
                {
                    cascades[j] = Transfer(input, output, 3);
                    if (Transfer(input, output, 1) != 0)
                        cascades[j] |= Transfer(input, output, 5) << 3;
                }
                foreach (uint cascade in cascades)
                    for (int j = 0; j < 8; j++)
                        if ((cascade & (1u << j)) != 0)
                            Require(Transfer(input, output, 8) < bookCount, "Invalid Vorbis residue book.");
            }

            int mappingCount = (int)Transfer(input, output, 6) + 1;
            for (int i = 0; i < mappingCount; i++)
            {
                Require(input.Read(16) == 0, "Unsupported Vorbis mapping type.");
                int submaps = Transfer(input, output, 1) != 0 ? (int)Transfer(input, output, 4) + 1 : 1;
                if (Transfer(input, output, 1) != 0)
                {
                    int couplingSteps = (int)Transfer(input, output, 8) + 1;
                    int channelBits = BitOperations.Log2((uint)channels - 1) + 1;
                    if (channels == 1) channelBits = 0;
                    for (int j = 0; j < couplingSteps; j++)
                    {
                        uint magnitude = Transfer(input, output, channelBits);
                        uint angle = Transfer(input, output, channelBits);
                        Require(magnitude < channels && angle < channels && magnitude != angle, "Invalid Vorbis channel coupling.");
                    }
                }
                Require(Transfer(input, output, 2) == 0, "Invalid Vorbis mapping reserved bits.");
                if (submaps > 1)
                    for (int j = 0; j < channels; j++)
                        Require(Transfer(input, output, 4) < submaps, "Invalid Vorbis channel mapping.");
                for (int j = 0; j < submaps; j++)
                {
                    Require(Transfer(input, output, 8) == 0, "Invalid Vorbis mapping time configuration.");
                    Require(Transfer(input, output, 8) < floorCount, "Invalid Vorbis floor mapping.");
                    Require(Transfer(input, output, 8) < residueCount, "Invalid Vorbis residue mapping.");
                }
            }

            int modeCount = (int)Transfer(input, output, 6) + 1;
            modeBlockFlags = new bool[modeCount];
            for (int i = 0; i < modeCount; i++)
            {
                modeBlockFlags[i] = Transfer(input, output, 1) != 0;
                Require(input.Read(16) == 0 && input.Read(16) == 0, "Unsupported Vorbis mode transform.");
                Require(Transfer(input, output, 8) < mappingCount, "Invalid Vorbis mode mapping.");
            }
            Require(input.Read(1) == 1, "Missing Vorbis setup framing bit.");
            Require(input.RemainingBits < 8 && input.Read(input.RemainingBits) == 0, "Unexpected data after the Vorbis setup.");
            return output.ToArray();
        }

        private static Dictionary<string, int> LoadCodebooks(string fileName)
        {
            byte[] packed = File.ReadAllBytes(Path.Combine(AppDirectories.ExecFolder, fileName));
            Require(packed.Length >= 8, "The bundled Wwise codebook library is truncated.");
            int tableStart = checked((int)BitConverter.ToUInt32(packed, packed.Length - 4));
            Require(tableStart > 0 && tableStart < packed.Length - 4 && (packed.Length - tableStart) % 4 == 0,
                "The bundled Wwise codebook library has an invalid index.");
            int count = (packed.Length - tableStart) / 4 - 1;
            Require(count <= 1024, "Too many Wwise codebooks.");
            var result = new Dictionary<string, int>(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int start = checked((int)BitConverter.ToUInt32(packed, tableStart + i * 4));
                int end = checked((int)BitConverter.ToUInt32(packed, tableStart + (i + 1) * 4));
                Require(start >= 0 && start < end && end <= tableStart, "Invalid Wwise codebook offset.");
                string signature = ReadCodebook(new BitReader(packed.AsSpan(start, end - start).ToArray()), true);
                result.TryAdd(signature, i);
            }
            return result;
        }

        private static string ReadCodebook(BitReader input, bool packed)
        {
            if (!packed)
                Require(input.Read(24) == 0x564342, "Invalid Vorbis codebook signature.");
            int dimensions = (int)input.Read(packed ? 4 : 16);
            int entries = (int)input.Read(packed ? 14 : 24);
            Require(dimensions > 0 && entries > 0 && entries <= 1 << 20, "Invalid Vorbis codebook dimensions.");
            using var canonical = new MemoryStream();
            using var writer = new BinaryWriter(canonical, Encoding.UTF8, true);
            writer.Write(dimensions);
            writer.Write(entries);
            if (input.Read(1) != 0)
            {
                int length = (int)input.Read(5) + 1;
                int position = 0;
                while (position < entries)
                {
                    Require(length <= 32, "Invalid Vorbis codeword length.");
                    int count = (int)input.Read(BitOperations.Log2((uint)(entries - position)) + 1);
                    Require(count <= entries - position, "Invalid ordered Vorbis codebook.");
                    for (int i = 0; i < count; i++) writer.Write((byte)length);
                    position += count;
                    length++;
                }
            }
            else
            {
                int lengthBits = packed ? (int)input.Read(3) : 5;
                Require(lengthBits is >= 1 and <= 5, "Invalid packed Vorbis codeword size.");
                bool sparse = input.Read(1) != 0;
                for (int i = 0; i < entries; i++)
                    writer.Write((byte)(!sparse || input.Read(1) != 0 ? input.Read(lengthBits) + 1 : 0));
            }

            uint lookupType = input.Read(packed ? 1 : 4);
            Require(lookupType <= 1, "Unsupported Vorbis codebook lookup type.");
            writer.Write(lookupType);
            if (lookupType == 1)
            {
                writer.Write(input.Read(32)); // Vorbis float minimum
                writer.Write(input.Read(32)); // Vorbis float delta
                int valueBits = (int)input.Read(4) + 1;
                writer.Write(valueBits);
                writer.Write(input.Read(1));
                int values = (int)Math.Pow(entries, 1.0 / dimensions);
                while (PowerAtMost(values + 1, dimensions, entries)) values++;
                while (!PowerAtMost(values, dimensions, entries)) values--;
                for (int i = 0; i < values; i++) writer.Write(input.Read(valueBits));
            }
            writer.Flush();
            return Convert.ToHexString(SHA256.HashData(canonical.GetBuffer().AsSpan(0, (int)canonical.Length)));
        }

        private static bool PowerAtMost(int value, int exponent, int limit)
        {
            long result = 1;
            for (int i = 0; i < exponent; i++)
            {
                result *= value;
                if (result > limit) return false;
            }
            return true;
        }

        private static uint Transfer(BitReader input, BitWriter output, int bits)
        {
            uint value = input.Read(bits);
            output.Write(value, bits);
            return value;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }

        private sealed class BitReader(byte[] bytes, int position = 0)
        {
            private int _position = position;
            public int RemainingBits => bytes.Length * 8 - _position;

            public uint Read(int bits)
            {
                Require(bits is >= 0 and <= 32 && bits <= RemainingBits, "The Vorbis setup packet is truncated.");
                uint result = 0;
                for (int i = 0; i < bits; i++, _position++)
                    result |= (uint)((bytes[_position / 8] >> (_position % 8)) & 1) << i;
                return result;
            }
        }

        private sealed class BitWriter
        {
            private readonly List<byte> _bytes = [];
            private int _position;

            public void Write(uint value, int bits)
            {
                for (int i = 0; i < bits; i++, _position++)
                {
                    if (_position / 8 == _bytes.Count) _bytes.Add(0);
                    _bytes[_position / 8] |= (byte)(((value >> i) & 1) << (_position % 8));
                }
            }

            public byte[] ToArray() => _bytes.ToArray();
        }
    }
}
