using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorerCore.Save;

/// <summary>
/// Reads the RON types in TSE's HeadMorph schema, independently of pretty-printing, field order
/// or array index comments. This is deliberately a schema reader, not a general RON deserializer.
/// </summary>
internal sealed class HeadMorphRonReader(string text)
{
    private readonly string input = text ?? throw new ArgumentNullException(nameof(text));
    private int position;

    public HeadMorph Read()
    {
        var morph = new HeadMorph();
        ReadStruct("HeadMorph", new Dictionary<string, Action>
        {
            ["hair_mesh"] = () => morph.HairMesh = ReadString(),
            ["accessory_mesh"] = () => morph.AccessoryMeshes = ReadList(ReadString),
            ["morph_features"] = () => morph.MorphFeatures = ReadMap(ReadFloat),
            ["offset_bones"] = () => morph.OffsetBones = ReadMap(ReadVector),
            ["lod0_vertices"] = () => morph.Lod0Vertices = ReadList(ReadVector),
            ["lod1_vertices"] = () => morph.Lod1Vertices = ReadList(ReadVector),
            ["lod2_vertices"] = () => morph.Lod2Vertices = ReadList(ReadVector),
            ["lod3_vertices"] = () => morph.Lod3Vertices = ReadList(ReadVector),
            ["scalar_parameters"] = () => morph.ScalarParameters = ReadMap(ReadFloat),
            ["vector_parameters"] = () => morph.VectorParameters = ReadMap(ReadColor),
            ["texture_parameters"] = () => morph.TextureParameters = ReadMap(ReadString)
        });
        SkipTrivia();
        if (position != input.Length) throw Error("Unexpected text after the head morph");
        return morph;
    }

    private Vector3 ReadVector()
    {
        var vector = new Vector3();
        ReadStruct("Vector", new Dictionary<string, Action>
        {
            ["x"] = () => vector.X = ReadFloat(),
            ["y"] = () => vector.Y = ReadFloat(),
            ["z"] = () => vector.Z = ReadFloat()
        });
        return vector;
    }

    private LinearColor ReadColor()
    {
        StartStruct("LinearColor");
        float r = ReadFloat(); Expect(',');
        float g = ReadFloat(); Expect(',');
        float b = ReadFloat(); Expect(',');
        float a = ReadFloat();
        Take(','); Expect(')');
        return new LinearColor(r, g, b, a);
    }

    private void ReadStruct(string name, Dictionary<string, Action> fields)
    {
        StartStruct(name);
        var seen = new HashSet<string>();
        ReadElements(')', () =>
        {
            string field = ReadIdentifier();
            if (!fields.TryGetValue(field, out Action read)) throw Error($"Unknown {name} field '{field}'");
            if (!seen.Add(field)) throw Error($"Duplicate {name} field '{field}'");
            Expect(':');
            read();
        });
        foreach (string field in fields.Keys)
            if (!seen.Contains(field)) throw Error($"Missing {name} field '{field}'");
    }

    private void StartStruct(string name)
    {
        SkipTrivia();
        if (position < input.Length && char.IsLetter(input[position]) && ReadIdentifier() != name)
            throw Error($"Expected {name}");
        Expect('(');
    }

    private List<T> ReadList<T>(Func<T> read)
    {
        var result = new List<T>();
        Expect('[');
        ReadElements(']', () => result.Add(read()));
        return result;
    }

    private Dictionary<string, T> ReadMap<T>(Func<T> read)
    {
        var result = new Dictionary<string, T>();
        Expect('{');
        ReadElements('}', () =>
        {
            string key = ReadString();
            Expect(':');
            if (!result.TryAdd(key, read())) throw Error($"Duplicate map key '{key}'");
        });
        return result;
    }

    private void ReadElements(char end, Action read)
    {
        while (!Take(end))
        {
            read();
            if (Take(end)) return;
            Expect(',');
        }
    }

    private string ReadIdentifier()
    {
        SkipTrivia();
        int start = position;
        while (position < input.Length && (char.IsLetterOrDigit(input[position]) || input[position] == '_')) position++;
        if (start == position) throw Error("Expected a field name");
        return input[start..position];
    }

    private float ReadFloat()
    {
        SkipTrivia();
        int start = position;
        while (position < input.Length && (char.IsAsciiLetterOrDigit(input[position]) || input[position] is '+' or '-' or '.' or '_')) position++;
        string token = input[start..position].Replace("_", "");
        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
            throw Error($"Expected a finite number, got '{token}'");
        return value;
    }

    private string ReadString()
    {
        SkipTrivia();
        // RON also permits Rust raw strings, useful for hand-edited asset paths.
        if (position < input.Length && input[position] == 'r')
        {
            position++;
            int hashes = 0;
            while (position < input.Length && input[position] == '#') { hashes++; position++; }
            Expect('"');
            string end = "\"" + new string('#', hashes);
            int closing = input.IndexOf(end, position, StringComparison.Ordinal);
            if (closing < 0) throw Error("Unterminated raw string");
            string raw = input[position..closing];
            position = closing + end.Length;
            return raw;
        }
        Expect('"');
        var result = new StringBuilder();
        while (position < input.Length)
        {
            char character = input[position++];
            if (character == '"') return result.ToString();
            if (character != '\\') { result.Append(character); continue; }
            if (position == input.Length) throw Error("Unterminated string escape");
            char escape = input[position++];
            switch (escape)
            {
                case '"': result.Append('"'); break;
                case '\\': result.Append('\\'); break;
                case 'n': result.Append('\n'); break;
                case 'r': result.Append('\r'); break;
                case 't': result.Append('\t'); break;
                case '0': result.Append('\0'); break;
                case 'u':
                    if (position == input.Length || input[position++] != '{') throw Error("Expected '{' in Unicode escape");
                    int start = position;
                    while (position < input.Length && Uri.IsHexDigit(input[position])) position++;
                    if (position == input.Length || input[position++] != '}'
                        || !int.TryParse(input.AsSpan(start, position - start - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint)
                        || !Rune.IsValid(codePoint)) throw Error("Invalid Unicode escape");
                    result.Append(char.ConvertFromUtf32(codePoint));
                    break;
                default: throw Error($"Unsupported string escape '\\{escape}'");
            }
        }
        throw Error("Unterminated string");
    }

    private bool Take(char character)
    {
        SkipTrivia();
        if (position >= input.Length || input[position] != character) return false;
        position++;
        return true;
    }

    private void Expect(char character)
    {
        if (!Take(character)) throw Error($"Expected '{character}'");
    }

    private void SkipTrivia()
    {
        while (position < input.Length)
        {
            if (char.IsWhiteSpace(input[position]) || input[position] == '\uFEFF') { position++; continue; }
            if (position + 1 >= input.Length || input[position] != '/') return;
            if (input[position + 1] == '/')
            {
                position += 2;
                while (position < input.Length && input[position] != '\n') position++;
            }
            else if (input[position + 1] == '*')
            {
                position += 2;
                int depth = 1;
                while (depth > 0)
                {
                    if (position + 1 >= input.Length) throw Error("Unterminated block comment");
                    if (input[position] == '/' && input[position + 1] == '*') { depth++; position += 2; }
                    else if (input[position] == '*' && input[position + 1] == '/') { depth--; position += 2; }
                    else position++;
                }
            }
            else return;
        }
    }

    private InvalidDataException Error(string message)
    {
        int line = 1, column = 1;
        for (int i = 0; i < position; i++)
            if (input[i] == '\n') { line++; column = 1; } else column++;
        return new InvalidDataException($"Invalid head morph RON at line {line}, column {column}: {message}.");
    }
}
