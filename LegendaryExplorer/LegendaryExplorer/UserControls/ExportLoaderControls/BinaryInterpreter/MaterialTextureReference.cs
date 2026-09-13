using System;
using System.Collections.Generic;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

// Mesh texture rows refer to slots in a material export, not offsets in the mesh binary.
internal sealed class MaterialTextureReference(ExportEntry material, string collectionName, int index, string label)
{
    public ExportEntry Material { get; } = material;
    public string Label { get; } = label;

    public int ReadIndex() => collectionName switch
    {
        "TextureParameterValues" => Material.GetProperty<ArrayProperty<StructProperty>>(collectionName)[index]
            .GetProp<ObjectProperty>("ParameterValue").Value,
        "ReferencedTextures" => Material.GetProperty<ArrayProperty<ObjectProperty>>(collectionName)[index].Value,
        _ => ObjectBinary.From<Material>(Material).SM3MaterialResource.UniformExpressionTextures[index]
    };

    public void WriteIndex(int value)
    {
        if (value != 0 && Material.FileRef.GetEntry(value)?.IsA("Texture") != true)
            throw new ArgumentException("Select a texture from this package, or 0 for None.");
        if (value == ReadIndex())
            return;

        switch (collectionName)
        {
            case "TextureParameterValues":
                var parameters = Material.GetProperty<ArrayProperty<StructProperty>>(collectionName);
                parameters[index].GetProp<ObjectProperty>("ParameterValue").Value = value;
                Material.WriteProperty(parameters);
                break;
            case "ReferencedTextures":
                var textures = Material.GetProperty<ArrayProperty<ObjectProperty>>(collectionName);
                textures[index].Value = value;
                Material.WriteProperty(textures);
                break;
            default:
                var binary = ObjectBinary.From<Material>(Material);
                binary.SM3MaterialResource.UniformExpressionTextures[index] = value;
                if (Material.Game < MEGame.ME3)
                    binary.SM3MaterialResource.Uniform2DTextureExpressions[index].TextureIndex = value;
                Material.WriteBinary(binary);
                break;
        }
    }

    public static IEnumerable<MaterialTextureReference> Enumerate(ExportEntry material)
    {
        var visited = new HashSet<ExportEntry>();
        // Only follow local parents: indices from another package cannot be edited here.
        while (material != null && visited.Add(material))
        {
            var props = material.GetProperties();
            if (material.ClassName == "Material")
            {
                var textures = ObjectBinary.From<Material>(material).SM3MaterialResource.UniformExpressionTextures;
                for (int i = 0; i < textures.Length; i++)
                    yield return new MaterialTextureReference(material, "UniformExpressionTextures", i, $"Texture[{i}]");
            }
            else if (material.IsA("MaterialInstanceConstant"))
            {
                if (props.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues") is { } parameters)
                {
                    for (int i = 0; i < parameters.Count; i++)
                    {
                        if (parameters[i].GetProp<ObjectProperty>("ParameterValue") != null)
                        {
                            string name = parameters[i].GetProp<NameProperty>("ParameterName")?.Value.Instanced ?? $"Texture[{i}]";
                            yield return new MaterialTextureReference(material, "TextureParameterValues", i, name);
                        }
                    }
                }
                if (props.GetProp<ArrayProperty<ObjectProperty>>("ReferencedTextures") is { } references)
                {
                    for (int i = 0; i < references.Count; i++)
                        yield return new MaterialTextureReference(material, "ReferencedTextures", i, $"ReferencedTexture[{i}]");
                }
            }

            var parent = props.GetProp<ObjectProperty>(material.ClassName == "RvrEffectsMaterialUser" ? "m_pBaseMaterial" : "Parent");
            material = parent == null ? null : material.FileRef.GetEntry(parent.Value) as ExportEntry;
        }
    }
}
