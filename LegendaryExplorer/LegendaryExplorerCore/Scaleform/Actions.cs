using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LegendaryExplorerCore.Scaleform
{
    /// <summary>
    /// Lossless AVM1 (ActionScript 1/2) action assembly. This is a bytecode editor, not an
    /// ActionScript source compiler or a verifier of script behavior or runtime stack types.
    /// Action layouts follow Adobe's SWF File Format Specification, version 19, chapter 5.
    /// </summary>
    public static class ScaleformActionScript
    {
        private const int MaximumNesting = 128;
        private static readonly Dictionary<byte, string> Names = new()
        {
            [0x00] = "End", [0x04] = "NextFrame", [0x05] = "PreviousFrame", [0x06] = "Play",
            [0x07] = "Stop", [0x08] = "ToggleQuality", [0x09] = "StopSounds", [0x0A] = "Add",
            [0x0B] = "Subtract", [0x0C] = "Multiply", [0x0D] = "Divide", [0x0E] = "Equals",
            [0x0F] = "Less", [0x10] = "And", [0x11] = "Or", [0x12] = "Not",
            [0x13] = "StringEquals", [0x14] = "StringLength", [0x15] = "StringExtract",
            [0x17] = "Pop", [0x18] = "ToInteger", [0x1C] = "GetVariable", [0x1D] = "SetVariable",
            [0x20] = "SetTarget2", [0x21] = "StringAdd", [0x22] = "GetProperty", [0x23] = "SetProperty",
            [0x24] = "CloneSprite", [0x25] = "RemoveSprite", [0x26] = "Trace", [0x27] = "StartDrag",
            [0x28] = "EndDrag", [0x29] = "StringLess", [0x2A] = "Throw", [0x2B] = "CastOp",
            [0x2C] = "ImplementsOp", [0x30] = "RandomNumber", [0x31] = "MBStringLength",
            [0x32] = "CharToAscii", [0x33] = "AsciiToChar", [0x34] = "GetTime",
            [0x35] = "MBStringExtract", [0x36] = "MBCharToAscii", [0x37] = "MBAsciiToChar",
            [0x3A] = "Delete", [0x3B] = "Delete2", [0x3C] = "DefineLocal", [0x3D] = "CallFunction",
            [0x3E] = "Return", [0x3F] = "Modulo", [0x40] = "NewObject", [0x41] = "DefineLocal2",
            [0x42] = "InitArray", [0x43] = "InitObject", [0x44] = "TypeOf", [0x45] = "TargetPath",
            [0x46] = "Enumerate", [0x47] = "Add2", [0x48] = "Less2", [0x49] = "Equals2",
            [0x4A] = "ToNumber", [0x4B] = "ToString", [0x4C] = "PushDuplicate", [0x4D] = "StackSwap",
            [0x4E] = "GetMember", [0x4F] = "SetMember", [0x50] = "Increment", [0x51] = "Decrement",
            [0x52] = "CallMethod", [0x53] = "NewMethod", [0x54] = "InstanceOf", [0x55] = "Enumerate2",
            [0x60] = "BitAnd", [0x61] = "BitOr", [0x62] = "BitXor", [0x63] = "BitLShift",
            [0x64] = "BitRShift", [0x65] = "BitURShift", [0x66] = "StrictEquals", [0x67] = "Greater",
            [0x68] = "StringGreater", [0x69] = "Extends", [0x81] = "GotoFrame", [0x83] = "GetURL",
            [0x87] = "StoreRegister", [0x88] = "ConstantPool", [0x89] = "StrictMode",
            [0x8A] = "WaitForFrame", [0x8B] = "SetTarget", [0x8C] = "GotoLabel", [0x8D] = "WaitForFrame2",
            [0x8E] = "DefineFunction2", [0x8F] = "Try", [0x94] = "With", [0x96] = "Push",
            [0x99] = "Jump", [0x9A] = "GetURL2", [0x9B] = "DefineFunction", [0x9D] = "If",
            [0x9E] = "Call", [0x9F] = "GotoFrame2"
        };

        /// <summary>
        /// Produces editable assembly with symbolic branch targets and raw operands. Container
        /// actions have two hex fields: their header operands, then their nested action bytes.
        /// Unknown opcodes, original string encodings, floating-point bits, and zero padding survive.
        /// Throws <see cref="InvalidDataException"/> for structurally invalid action streams.
        /// </summary>
        public static string Disassemble(ReadOnlySpan<byte> actions)
        {
            var records = ReadBlock(actions, true, 0, 0);
            var result = new StringBuilder();
            result.AppendLine("; AVM1 bytecode assembly. Hex operands preserve the original bytes.");
            result.AppendLine("; Labels relocate Jump/If targets when instruction lengths change.");
            result.AppendLine("; Containers: opcode Name | header hex | nested body hex (body size fields must match).");
            result.AppendLine("; String shortcuts: .pushstring \"text\" and .constantpool \"a\", \"b\" (UTF-8).");
            foreach (var record in records)
            {
                result.Append(Label(record.Start)).Append(": ").Append(record.Opcode.ToString("X2"))
                    .Append(' ').Append(Name(record.Opcode));
                var payload = actions.Slice(record.PayloadOffset, record.PayloadLength);
                if (record.Opcode is 0x99 or 0x9D)
                {
                    result.Append(" -> ").Append(Label(record.End + BinaryPrimitives.ReadInt16LittleEndian(payload)));
                }
                else if (record.Opcode >= 0x80)
                {
                    result.Append(" | ").Append(Hex(payload));
                    if (IsContainer(record.Opcode))
                        result.Append(" | ").Append(Hex(actions.Slice(record.PayloadOffset + record.PayloadLength, record.BodyLength)));
                    AppendDescription(result, record.Opcode, payload);
                }
                result.AppendLine();
            }
            result.Append(Label(actions.Length)).AppendLine(":");
            return result.ToString();
        }

        /// <summary>
        /// Compiles bytecode assembly and validates record lengths, typed push operands, nested
        /// block bounds, termination, and branch targets. It does not compile ActionScript source.
        /// Raw hex operands are never transcoded. The .pushstring/.constantpool shortcuts use UTF-8.
        /// </summary>
        public static byte[] Assemble(string assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            var entries = new List<AssemblyEntry>();
            int offset = 0;
            var lines = assembly.Replace("\r", "").Split('\n');
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                try
                {
                    string line = StripComment(lines[lineIndex]).Trim();
                    if (line.Length == 0) continue;
                    int colon = line.IndexOf(':');
                    // A colon in a string shortcut is part of the literal, not a label.
                    int firstSpace = line.IndexOfAny([' ', '\t']);
                    if (colon >= 0 && (firstSpace < 0 || colon < firstSpace))
                    {
                        string label = line[..colon].Trim();
                        if (!IsLabel(label) || !labels.TryAdd(label, offset))
                            throw new InvalidDataException($"Invalid or duplicate label '{label}'.");
                        line = line[(colon + 1)..].Trim();
                        if (line.Length == 0) continue;
                    }

                    AssemblyEntry entry;
                    if (line.StartsWith(".pushstring", StringComparison.OrdinalIgnoreCase) && IsDirective(line, 11))
                    {
                        string value = JsonSerializer.Deserialize<string>(line[11..].Trim());
                        entry = new AssemblyEntry(0x96, EncodeString(value, true), [], null, offset, lineIndex + 1);
                    }
                    else if (line.StartsWith(".constantpool", StringComparison.OrdinalIgnoreCase) && IsDirective(line, 13))
                    {
                        var strings = JsonSerializer.Deserialize<string[]>("[" + line[13..].Trim() + "]");
                        if (strings.Length > ushort.MaxValue) throw new InvalidDataException("Too many constants.");
                        using var operands = new MemoryStream();
                        operands.WriteByte((byte)strings.Length);
                        operands.WriteByte((byte)(strings.Length >> 8));
                        foreach (var value in strings) operands.Write(EncodeString(value, false));
                        entry = new AssemblyEntry(0x88, operands.ToArray(), [], null, offset, lineIndex + 1);
                    }
                    else
                    {
                        string[] fields = line.Split('|');
                        if (fields.Length > 3) throw new InvalidDataException("Expected at most two hex operand fields.");
                        string instruction = fields[0].Trim();
                        int arrow = instruction.IndexOf("->", StringComparison.Ordinal);
                        string target = arrow < 0 ? null : instruction[(arrow + 2)..].Trim();
                        if (arrow >= 0) instruction = instruction[..arrow].Trim();
                        var words = instruction.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                        if (words.Length is < 1 or > 2) throw new InvalidDataException("Expected an opcode and optional mnemonic.");
                        byte opcode;
                        if (!byte.TryParse(words[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out opcode))
                        {
                            var match = Names.FirstOrDefault(pair => pair.Value.Equals(words[0], StringComparison.OrdinalIgnoreCase));
                            if (match.Value == null) throw new InvalidDataException($"Unknown opcode '{words[0]}'.");
                            opcode = match.Key;
                        }
                        if (words.Length == 2 && !Name(opcode).Equals(words[1], StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Opcode {opcode:X2} is {Name(opcode)}, not '{words[1]}'.");
                        if (target != null && (opcode is not (0x99 or 0x9D) || !IsLabel(target) || fields.Length != 1))
                            throw new InvalidDataException("Only Jump/If accept a single symbolic branch target.");
                        byte[] payload = target != null ? new byte[2] : fields.Length >= 2 ? ParseHex(fields[1]) : [];
                        byte[] body = fields.Length == 3 ? ParseHex(fields[2]) : [];
                        if (opcode < 0x80 && (payload.Length != 0 || body.Length != 0))
                            throw new InvalidDataException("Short action opcodes cannot have operands.");
                        if (!IsContainer(opcode) && body.Length != 0)
                            throw new InvalidDataException("Only container actions accept a nested body field.");
                        entry = new AssemblyEntry(opcode, payload, body, target, offset, lineIndex + 1);
                    }
                    if (entry.Payload.Length > ushort.MaxValue)
                        throw new InvalidDataException("An action operand field cannot exceed 65535 bytes.");
                    entries.Add(entry);
                    offset = checked(offset + (entry.Opcode >= 0x80 ? 3 : 1) + entry.Payload.Length + entry.Body.Length);
                }
                catch (Exception exception) when (exception is InvalidDataException or FormatException or JsonException or OverflowException or ArgumentException)
                {
                    throw new InvalidDataException($"Assembly line {lineIndex + 1}: {exception.Message}", exception);
                }
            }

            using var output = new MemoryStream(offset);
            foreach (var entry in entries)
            {
                if (entry.Target != null)
                {
                    if (!labels.TryGetValue(entry.Target, out int target))
                        throw new InvalidDataException($"Assembly line {entry.Line}: Undefined label '{entry.Target}'.");
                    int relativeOffset = target - entry.Offset - 5;
                    if (relativeOffset < short.MinValue || relativeOffset > short.MaxValue)
                        throw new InvalidDataException($"Assembly line {entry.Line}: Branch target exceeds the signed 16-bit range.");
                    BinaryPrimitives.WriteInt16LittleEndian(entry.Payload, (short)relativeOffset);
                }
                output.WriteByte(entry.Opcode);
                if (entry.Opcode >= 0x80)
                {
                    output.WriteByte((byte)entry.Payload.Length);
                    output.WriteByte((byte)(entry.Payload.Length >> 8));
                }
                output.Write(entry.Payload);
                output.Write(entry.Body);
            }
            byte[] result = output.ToArray();
            Validate(result);
            return result;
        }

        /// <summary>
        /// Checks AVM1 binary structure, including nested function/With/Try bodies and Jump/If
        /// targets. Unknown opcodes remain opaque; stack behavior and game compatibility require
        /// runtime testing. DoInitAction callers must remove the leading two-byte sprite ID first.
        /// </summary>
        public static void Validate(byte[] actions)
        {
            ArgumentNullException.ThrowIfNull(actions);
            ReadBlock(actions, true, 0, 0);
        }

        private static List<ActionRecord> ReadBlock(ReadOnlySpan<byte> data, bool requireEnd, int depth, int baseOffset)
        {
            if (depth > MaximumNesting) throw Error(baseOffset, "Action blocks exceed the nesting limit.");
            var records = new List<ActionRecord>();
            bool ended = false;
            int position = 0;
            while (position < data.Length)
            {
                int start = position;
                byte opcode = data[position++];
                if (ended && opcode != 0) throw Error(baseOffset + start, "Nonzero bytes follow End.");
                int length = 0;
                if (opcode >= 0x80)
                {
                    Require(data, position, 2, baseOffset);
                    length = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
                    position += 2;
                }
                Require(data, position, length, baseOffset);
                int payloadOffset = position;
                var payload = data.Slice(position, length);
                int[] bodySizes = ValidateOperands(opcode, payload, baseOffset + position);
                position += length;
                int bodyLength = 0;
                foreach (int bodySize in bodySizes)
                {
                    Require(data, position, bodySize, baseOffset);
                    ReadBlock(data.Slice(position, bodySize), false, depth + 1, baseOffset + position);
                    position += bodySize;
                    bodyLength += bodySize;
                }
                records.Add(new ActionRecord(opcode, start, payloadOffset, length, bodyLength, position));
                ended |= opcode == 0;
            }
            if (requireEnd && !ended) throw Error(baseOffset + position, "Action stream is missing End (00).");

            var boundaries = new HashSet<int>(records.Select(record => record.Start)) { data.Length };
            foreach (var record in records)
            {
                if (record.Opcode is not (0x99 or 0x9D)) continue;
                int target = record.End + BinaryPrimitives.ReadInt16LittleEndian(data[record.PayloadOffset..]);
                if (!boundaries.Contains(target))
                    throw Error(baseOffset + record.Start, $"Branch target 0x{baseOffset + target:X8} is not an action boundary in this block.");
            }
            return records;
        }

        private static int[] ValidateOperands(byte opcode, ReadOnlySpan<byte> payload, int baseOffset)
        {
            int position = 0;
            int[] bodies = [];
            switch (opcode)
            {
                case 0x81: case 0x99: case 0x9D:
                    position = 2;
                    break;
                case 0x87: case 0x89: case 0x8D: case 0x9A:
                    position = 1;
                    break;
                case 0x8A:
                    position = 3;
                    break;
                case 0x83:
                    ReadString(payload, ref position, baseOffset);
                    ReadString(payload, ref position, baseOffset);
                    break;
                case 0x8B: case 0x8C:
                    ReadString(payload, ref position, baseOffset);
                    break;
                case 0x88:
                    int count = ReadUInt16(payload, ref position, baseOffset);
                    for (int i = 0; i < count; i++) ReadString(payload, ref position, baseOffset);
                    break;
                case 0x8E: case 0x9B:
                    ReadString(payload, ref position, baseOffset);
                    int parameters = ReadUInt16(payload, ref position, baseOffset);
                    if (opcode == 0x8E)
                    {
                        Require(payload, position, 3, baseOffset);
                        position += 3; // Register count and preload/suppress flags.
                    }
                    for (int i = 0; i < parameters; i++)
                    {
                        if (opcode == 0x8E) { Require(payload, position, 1, baseOffset); position++; }
                        ReadString(payload, ref position, baseOffset);
                    }
                    bodies = [ReadUInt16(payload, ref position, baseOffset)];
                    break;
                case 0x8F:
                    Require(payload, position, 1, baseOffset);
                    byte flags = payload[position++];
                    bodies = [ReadUInt16(payload, ref position, baseOffset), ReadUInt16(payload, ref position, baseOffset), ReadUInt16(payload, ref position, baseOffset)];
                    if ((flags & 4) != 0) { Require(payload, position, 1, baseOffset); position++; }
                    else ReadString(payload, ref position, baseOffset);
                    break;
                case 0x94:
                    bodies = [ReadUInt16(payload, ref position, baseOffset)];
                    break;
                case 0x96:
                    while (position < payload.Length)
                    {
                        byte type = payload[position++];
                        int size = type switch { 1 or 7 => 4, 2 or 3 => 0, 4 or 5 or 8 => 1, 6 => 8, 9 => 2, _ => -1 };
                        if (type == 0) ReadString(payload, ref position, baseOffset);
                        else
                        {
                            if (size < 0) throw Error(baseOffset + position - 1, $"Unknown Push value type {type}.");
                            Require(payload, position, size, baseOffset);
                            position += size;
                        }
                    }
                    break;
                case 0x9E:
                    break;
                case 0x9F:
                    Require(payload, 0, 1, baseOffset);
                    position = (payload[0] & 2) != 0 ? 3 : 1;
                    break;
                default:
                    return bodies; // Unknown actions are opaque and retained exactly.
            }
            if (position != payload.Length)
                throw Error(baseOffset, $"{Name(opcode)} requires {position} operand bytes; its header declares {payload.Length}.");
            return bodies;
        }

        private static void AppendDescription(StringBuilder output, byte opcode, ReadOnlySpan<byte> payload)
        {
            // Descriptions are comments only; assembling never uses a decoded string in place of raw bytes.
            if (opcode is not (0x88 or 0x96)) return;
            var strings = new List<string>();
            int position = opcode == 0x88 ? 2 : 0;
            while (position < payload.Length && strings.Count < 8)
            {
                int type = opcode == 0x88 ? 0 : payload[position++];
                if (type == 0)
                {
                    int end = payload[position..].IndexOf((byte)0) + position;
                    strings.Add(JsonSerializer.Serialize(Encoding.UTF8.GetString(payload[position..end])));
                    position = end + 1;
                }
                else position += type switch { 1 or 7 => 4, 2 or 3 => 0, 4 or 5 or 8 => 1, 6 => 8, 9 => 2, _ => 0 };
            }
            if (strings.Count > 0) output.Append(" ; strings: ").Append(string.Join(", ", strings));
        }

        private static int ReadUInt16(ReadOnlySpan<byte> data, ref int position, int baseOffset)
        {
            Require(data, position, 2, baseOffset);
            int value = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
            position += 2;
            return value;
        }

        private static void ReadString(ReadOnlySpan<byte> data, ref int position, int baseOffset)
        {
            int length = data[position..].IndexOf((byte)0);
            if (length < 0) throw Error(baseOffset + position, "Unterminated string operand.");
            position += length + 1;
        }

        private static void Require(ReadOnlySpan<byte> data, int position, int count, int baseOffset)
        {
            if (position < 0 || count < 0 || position > data.Length - count)
                throw Error(baseOffset + position, "Truncated action record or nested action body.");
        }

        private static byte[] ParseHex(string text)
        {
            string compact = new(text.Where(character => !char.IsWhiteSpace(character)).ToArray());
            return Convert.FromHexString(compact);
        }

        private static byte[] EncodeString(string value, bool push)
        {
            if (value == null || value.Contains('\0')) throw new InvalidDataException("String literals must be non-null and cannot contain NUL.");
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            byte[] result = new byte[utf8.Length + (push ? 2 : 1)];
            utf8.CopyTo(result, push ? 1 : 0);
            return result;
        }

        private static string StripComment(string line)
        {
            bool quoted = false, escaped = false;
            for (int i = 0; i < line.Length; i++)
            {
                char value = line[i];
                if (escaped) { escaped = false; continue; }
                if (quoted && value == '\\') { escaped = true; continue; }
                if (value == '"') quoted = !quoted;
                else if (!quoted && value is ';' or '#') return line[..i];
            }
            return line;
        }

        private static bool IsDirective(string line, int length) => line.Length == length || char.IsWhiteSpace(line[length]);
        private static bool IsLabel(string value) => value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') && value.All(character => char.IsLetterOrDigit(character) || character == '_');
        private static bool IsContainer(byte opcode) => opcode is 0x8E or 0x8F or 0x94 or 0x9B;
        private static string Label(int offset) => $"L{offset:X8}";
        private static string Name(byte opcode) => Names.TryGetValue(opcode, out string name) ? name : $"Op_{opcode:X2}";
        private static string Hex(ReadOnlySpan<byte> bytes) => string.Join(" ", Convert.ToHexString(bytes).Chunk(2).Select(pair => new string(pair)));
        private static InvalidDataException Error(int offset, string message) => new($"AVM1 offset 0x{offset:X8}: {message}");

        private sealed record ActionRecord(byte Opcode, int Start, int PayloadOffset, int PayloadLength, int BodyLength, int End);
        private sealed record AssemblyEntry(byte Opcode, byte[] Payload, byte[] Body, string Target, int Offset, int Line);
    }
}
