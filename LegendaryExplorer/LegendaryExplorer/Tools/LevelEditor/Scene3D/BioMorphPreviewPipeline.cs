using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// Shared Level Editor/dialogue-preview application path for a cooked BioMorphFace. Geometry uses
/// the same BioMorphFace skeleton math as Morph Editor, and material overrides are applied to the
/// compiled in-game material proxies without translating or approximating skin parameters.
/// </summary>
internal static class BioMorphPreviewPipeline
{
    public static void Apply(SkinnedMeshRenderer renderer, SkeletalMesh skeletalMesh, int lod,
        ModelPreview<LEVertex> gameShaderMesh, ExportEntry morphExport, bool useStoredMorphLods,
        MeshRenderContext renderContext)
    {
        ApplyGeometry(renderer, skeletalMesh, lod, morphExport, useStoredMorphLods);
        ApplyMaterialOverrides(gameShaderMesh, morphExport, renderContext);
    }

    public static void ApplyGeometry(SkinnedMeshRenderer renderer, SkeletalMesh skeletalMesh, int lod,
        ExportEntry morphExport, bool useStoredMorphLods)
    {
        if (renderer is null || skeletalMesh is null || morphExport is null)
        {
            return;
        }

        (LegendaryExplorerCore.Unreal.Classes.BonePosition[] bonePositions, Vector3[][] morphLods) =
            LegendaryExplorerCore.Unreal.Classes.BioMorphFace.GetBoneAndVertexPositions(morphExport);
        Vector3[] morphPositions = useStoredMorphLods && morphLods?.Length > lod ? morphLods[lod] : null;
        renderer.ApplyMorph(skeletalMesh.RefSkeleton, bonePositions, morphPositions);
    }

    public static void ApplyMaterialOverrides(ModelPreview<LEVertex> gameShaderMesh, ExportEntry morphExport,
        MeshRenderContext renderContext)
    {
        if (gameShaderMesh is null
            || morphExport.GetProperty<ObjectProperty>("m_oMaterialOverrides")
                is not { Value: not 0 } materialOverrideProperty
            || renderContext.ResolveExportCached(morphExport.FileRef, materialOverrideProperty.Value)
                is not { } materialOverride)
        {
            return;
        }

        List<MaterialRenderProxy> materials = gameShaderMesh.Materials.Values
            .OfType<LEShaderPreviewMaterial>()
            .Select(value => value.RenderProxy)
            .Distinct()
            .ToList();
        PropertyCollection overrideProperties = materialOverride.GetProperties(
            packageCache: renderContext.PackageCache);
        foreach (MaterialRenderProxy material in materials)
        {
            material.ResetPreviewParameterOverrides();
            if (overrideProperties.GetProp<ArrayProperty<StructProperty>>("m_aScalarOverrides")
                is { } scalarOverrides)
            {
                foreach (StructProperty scalar in scalarOverrides)
                {
                    string name = scalar.GetProp<NameProperty>("nName")?.Value.Instanced;
                    if (!string.IsNullOrEmpty(name))
                    {
                        material.SetScalarParameter(name, scalar.GetProp<FloatProperty>("sValue")?.Value ?? 0f);
                    }
                }
            }
            if (overrideProperties.GetProp<ArrayProperty<StructProperty>>("m_aColorOverrides")
                is { } colorOverrides)
            {
                foreach (StructProperty color in colorOverrides)
                {
                    string name = color.GetProp<NameProperty>("nName")?.Value.Instanced;
                    if (!string.IsNullOrEmpty(name))
                    {
                        LinearColor value = color.GetProp<StructProperty>("cValue") is { } linearColor
                            ? CommonStructs.GetLinearColor(linearColor)
                            : LinearColor.White;
                        material.SetVectorParameter(name, value);
                    }
                }
            }
        }

        if (overrideProperties.GetProp<ArrayProperty<StructProperty>>("m_aTextureOverrides")
            is not { } textureOverrides)
        {
            return;
        }
        foreach (StructProperty texture in textureOverrides)
        {
            string name = texture.GetProp<NameProperty>("nName")?.Value.Instanced;
            IEntry textureEntry = texture.GetProp<ObjectProperty>("m_pTexture")
                ?.ResolveToEntry(materialOverride.FileRef);
            ExportEntry textureExport = textureEntry switch
            {
                ExportEntry export when export.IsTexture() => export,
                ImportEntry import => renderContext.ResolveExportCached(import),
                _ => null
            };
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            if (textureExport is not null && !textureExport.IsTexture())
            {
                textureExport = null;
            }
            PreviewTextureCache.TextureEntry cachedTexture = textureExport is not null
                ? renderContext.TextureCache.LoadTexture(textureExport, renderContext.PackageCache,
                    gameShaderMesh.UsesSrgbColorManagement)
                : null;
            foreach (MaterialRenderProxy material in materials)
            {
                material.SetTextureParameter(name, textureExport?.InstancedFullPath, cachedTexture);
            }
        }
    }
}
