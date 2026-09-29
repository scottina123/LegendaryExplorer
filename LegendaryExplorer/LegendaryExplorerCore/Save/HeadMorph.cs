using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace LegendaryExplorerCore.Save;

/// <summary>
/// Trilogy Save Editor's head morph interchange format. Schema:
/// https://github.com/KarlitosVII/trilogy-save-editor/blob/6451f23d1fba62358328dce4e87733970f36ebf1/src/save_data/shared/appearance.rs
/// </summary>
public class HeadMorph
{
    public string HairMesh { get; set; } = "None";
    public List<string> AccessoryMeshes { get; set; } = [];
    public Dictionary<string, float> MorphFeatures { get; set; } = [];
    public Dictionary<string, Vector3> OffsetBones { get; set; } = [];
    public List<Vector3> Lod0Vertices { get; set; } = [];
    public List<Vector3> Lod1Vertices { get; set; } = [];
    public List<Vector3> Lod2Vertices { get; set; } = [];
    public List<Vector3> Lod3Vertices { get; set; } = [];
    public Dictionary<string, float> ScalarParameters { get; set; } = [];
    public Dictionary<string, LinearColor> VectorParameters { get; set; } = [];
    public Dictionary<string, string> TextureParameters { get; set; } = [];

    public static HeadMorph FromRonFile(string ronFilePath) => FromRon(File.ReadAllText(ronFilePath));

    public static HeadMorph FromRon(string text) => new HeadMorphRonReader(text).Read();

    public void ToRonFile(string file) => File.WriteAllText(file, ToRon(), new UTF8Encoding(false));

    /// <summary>Writes the same struct, map, vector and color representation as ron::ser in TSE.</summary>
    public string ToRon()
    {
        var output = new StringBuilder("(\n");
        output.Append("    hair_mesh: ").Append(Quote(HairMesh ?? "None")).Append(",\n");
        WriteList("accessory_mesh", AccessoryMeshes, Quote);
        WriteMap("morph_features", MorphFeatures, Number);
        WriteMap("offset_bones", OffsetBones, Vector);
        WriteList("lod0_vertices", Lod0Vertices, Vector);
        WriteList("lod1_vertices", Lod1Vertices, Vector);
        WriteList("lod2_vertices", Lod2Vertices, Vector);
        WriteList("lod3_vertices", Lod3Vertices, Vector);
        WriteMap("scalar_parameters", ScalarParameters, Number);
        WriteMap("vector_parameters", VectorParameters,
            color => $"({Number(color.R)}, {Number(color.G)}, {Number(color.B)}, {Number(color.A)})");
        WriteMap("texture_parameters", TextureParameters, Quote);
        return output.Append(")\n").ToString();

        void WriteList<T>(string name, List<T> values, Func<T, string> format)
        {
            output.Append("    ").Append(name).Append(": [");
            if (values is { Count: > 0 })
            {
                output.Append('\n');
                for (int i = 0; i < values.Count; i++)
                    output.Append("        ").Append(format(values[i])).Append(", // [").Append(i).Append("]\n");
                output.Append("    ");
            }
            output.Append("],\n");
        }

        void WriteMap<T>(string name, Dictionary<string, T> values, Func<T, string> format)
        {
            output.Append("    ").Append(name).Append(": {");
            if (values is { Count: > 0 })
            {
                output.Append('\n');
                foreach (var (key, value) in values)
                    output.Append("        ").Append(Quote(key)).Append(": ").Append(format(value)).Append(",\n");
                output.Append("    ");
            }
            output.Append("},\n");
        }
    }

    private static string Vector(Vector3 value) => $"(x: {Number(value.X)}, y: {Number(value.Y)}, z: {Number(value.Z)})";

    private static string Number(float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Head morph values must be finite numbers.");
        string result = value.ToString("R", CultureInfo.InvariantCulture);
        return result.Contains('.') || result.Contains('E') || result.Contains('e') ? result : result + ".0";
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (char character in value ?? string.Empty)
        {
            result.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\0' => "\\0",
                _ when char.IsControl(character) => "\\u{" + ((int)character).ToString("x", CultureInfo.InvariantCulture) + "}",
                _ => character.ToString()
            });
        }
        return result.Append('"').ToString();
    }
}
