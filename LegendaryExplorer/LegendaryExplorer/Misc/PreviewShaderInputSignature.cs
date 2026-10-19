using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SharpDX.D3DCompiler;

namespace LegendaryExplorer.Misc;

/// <summary>
/// Keeps a recompiled preview pixel shader's input widths compatible with the cooked vertex shader.
/// </summary>
internal static class PreviewShaderInputSignature
{
    private static readonly Regex MainDeclaration = new(@"\bvoid\s+main\s*\((?<parameters>[^)]*)\)\s*\{", RegexOptions.CultureInvariant);
    private static readonly Regex InputDeclaration = new(
        @"^(?<prefix>\s*(?:(?:in|linear|centroid|nointerpolation|noperspective|sample|precise|const)\s+)*)(?<type>float|uint|int)(?<width>[1-4]?)\s+(?<name>[A-Za-z_]\w*)\s*:\s*(?<semantic>[A-Za-z_]\w*)(?<suffix>\s*)$",
        RegexOptions.CultureInvariant);

    internal static string Restore(string hlsl, byte[] originalShaderBytecode)
    {
        Match main = MainDeclaration.Match(hlsl);
        if (!main.Success)
        {
            return hlsl;
        }

        var widths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var reflection = new ShaderReflection(originalShaderBytecode))
        {
            for (int i = 0; i < reflection.Description.InputParameters; i++)
            {
                ShaderParameterDescription input = reflection.GetInputParameterDescription(i);
                // A packed semantic that starts beyond X already has an explicit split declaration
                // in 3Dmigoto's HLSL. Leave that layout alone rather than moving its components.
                int width = (int)input.UsageMask switch
                {
                    1 => 1,
                    3 => 2,
                    7 => 3,
                    15 => 4,
                    _ => 0
                };
                if (width > 0)
                {
                    widths[input.SemanticName + input.SemanticIndex] = width;
                }
            }
        }

        Group parameters = main.Groups["parameters"];
        string[] declarations = parameters.Value.Split(',');
        var locals = new StringBuilder();
        for (int i = 0; i < declarations.Length; i++)
        {
            Match declaration = InputDeclaration.Match(declarations[i]);
            if (!declaration.Success
                || !widths.TryGetValue(declaration.Groups["semantic"].Value, out int width))
            {
                continue;
            }

            string widthText = declaration.Groups["width"].Value;
            int declaredWidth = widthText.Length == 0 ? 1 : widthText[0] - '0';
            if (width >= declaredWidth)
            {
                continue;
            }

            string scalarType = declaration.Groups["type"].Value;
            string name = declaration.Groups["name"].Value;
            string inputName = "lexPreviewInput_" + name;
            while (hlsl.Contains(inputName, StringComparison.Ordinal))
            {
                inputName += "_";
            }
            string narrowedType = scalarType + (width == 1 ? "" : width.ToString());
            declarations[i] = declaration.Groups["prefix"].Value + narrowedType + " " + inputName
                + " : " + declaration.Groups["semantic"].Value + declaration.Groups["suffix"].Value;

            // Keep the decompiler's register-shaped local variable, including any wide swizzles.
            // Only the actual entry-point input is narrowed, and its unused components are zero.
            locals.Append("\n  ").Append(scalarType).Append(widthText).Append(' ').Append(name)
                .Append(" = ").Append(scalarType).Append(widthText).Append('(').Append(inputName);
            for (int component = width; component < declaredWidth; component++)
            {
                locals.Append(", 0");
            }
            locals.Append(");");
        }

        if (locals.Length == 0)
        {
            return hlsl;
        }

        int parametersEnd = parameters.Index + parameters.Length;
        int bodyStart = main.Index + main.Length;
        return hlsl[..parameters.Index] + string.Join(',', declarations)
            + hlsl[parametersEnd..bodyStart] + locals + hlsl[bodyStart..];
    }
}
