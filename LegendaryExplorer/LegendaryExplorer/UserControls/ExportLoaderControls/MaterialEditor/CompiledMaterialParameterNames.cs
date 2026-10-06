using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;

/// <summary>
/// Finds parameter definitions in cooked uniform expressions when the editor expression graph is absent.
/// </summary>
internal static class CompiledMaterialParameterNames
{
    internal static IReadOnlyList<NameReference> GetNames(ExportEntry material, string parameterArrayName,
        PackageCache cache = null, IMEPackage contextPackage = null)
    {
        var names = new List<NameReference>();
        if (material is null || material.IsDefaultObject || !IsSupportedArray(parameterArrayName))
            return names;

        MaterialResource firstResource;
        MaterialResource secondResource;
        StaticParameterSet firstParameterSet;
        StaticParameterSet secondParameterSet;
        try
        {
            if (material.IsA("Material"))
            {
                var binary = ObjectBinary.From<Material>(material, cache);
                firstResource = binary.SM3MaterialResource;
                secondResource = binary.SM2MaterialResource;
                firstParameterSet = firstResource is null ? null : (StaticParameterSet)firstResource.ID;
                secondParameterSet = secondResource is null ? null : (StaticParameterSet)secondResource.ID;
            }
            else if (material.IsA("MaterialInstance")
                     && material.GetProperty<BoolProperty>("bHasStaticPermutationResource", cache)?.Value == true)
            {
                var binary = ObjectBinary.From<MaterialInstance>(material, cache);
                firstResource = binary.SM3StaticPermutationResource;
                secondResource = binary.SM2StaticPermutationResource;
                firstParameterSet = binary.SM3StaticParameterSet;
                secondParameterSet = binary.SM2StaticParameterSet;
            }
            else
            {
                return names;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // New or incomplete exports may not have material binary data yet. Their property definitions
            // can still be used by the caller.
            Debug.WriteLine($"Could not read compiled parameters on {material.InstancedFullPath}: {exception.Message}");
            return names;
        }

        if (material.Game is MEGame.ME1 or MEGame.ME2)
        {
            AddResource(firstResource, material.Game, parameterArrayName, names);
            AddResource(secondResource, material.Game, parameterArrayName, names);
        }
        else
        {
            AddShaderMap(FindShaderMap(material, firstParameterSet, cache, contextPackage), parameterArrayName, names);
            if (secondParameterSet is not null && secondParameterSet != firstParameterSet)
                AddShaderMap(FindShaderMap(material, secondParameterSet, cache, contextPackage), parameterArrayName, names);
        }

        return names.OrderBy(name => name.Instanced, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsSupportedArray(string arrayName) =>
        arrayName is "ScalarParameterValues" or "VectorParameterValues" or "TextureParameterValues";

    private static MaterialShaderMap FindShaderMap(ExportEntry material, StaticParameterSet parameterSet,
        PackageCache cache, IMEPackage contextPackage)
    {
        if (parameterSet is null || parameterSet.BaseMaterialId == Guid.Empty)
            return null;

        // A material imported by the edited MIC can have its shader map embedded in that MIC's package.
        // Keep this package separate from PackageCache, which owns and disposes the packages inserted into it.
        IEnumerable<IMEPackage> packages = contextPackage is null
            ? new[] { material.FileRef }
            : new[] { contextPackage, material.FileRef };
        if (cache is not null)
            packages = packages.Concat(cache.Cache.Values);

        foreach (IMEPackage package in packages.Distinct())
        {
            if (package.Game != material.Game
                || package.FindExport("SeekFreeShaderCache", "ShaderCache") is not { } cacheExport)
                continue;

            try
            {
                var shaderCache = ObjectBinary.From<ShaderCache>(cacheExport, cache);
                if (shaderCache.MaterialShaderMaps.TryGetValue(parameterSet, out MaterialShaderMap shaderMap))
                    return shaderMap;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Debug.WriteLine($"Could not read material shader map in {package.FilePath}: {exception.Message}");
            }
        }

        // Stock cooked materials often refer to a shader map in the installed game's RefShaderCache.
        // This reader returns null when the game or requested shader map is unavailable.
        if (!material.Game.IsLEGame() && material.Game != MEGame.ME3)
            return null;
        try
        {
            return RefShaderCacheReader.GetMaterialShaderMap(material.Game, parameterSet, out _);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"Could not read reference shader parameters for {material.InstancedFullPath}: {exception.Message}");
            return null;
        }
    }

    private static void AddResource(MaterialResource resource, MEGame game, string parameterArrayName,
        List<NameReference> names)
    {
        if (resource is null)
            return;

        AddExpressions(resource.UniformPixelScalarExpressions, parameterArrayName, names);
        AddExpressions(resource.UniformPixelVectorExpressions, parameterArrayName, names);
        AddExpressions(resource.Uniform2DTextureExpressions, parameterArrayName, names);
        AddExpressions(resource.UniformCubeTextureExpressions, parameterArrayName, names);
        if (game == MEGame.ME1)
        {
            foreach (ME1MaterialUniformExpressionsElement element in resource.Me1MaterialUniformExpressionsList ?? [])
            {
                if (element is null)
                    continue;
                AddExpressions(element.UniformPixelScalarExpressions, parameterArrayName, names);
                AddExpressions(element.UniformPixelVectorExpressions, parameterArrayName, names);
                AddExpressions(element.Uniform2DTextureExpressions, parameterArrayName, names);
                AddExpressions(element.UniformCubeTextureExpressions, parameterArrayName, names);
            }
        }
    }

    private static void AddShaderMap(MaterialShaderMap shaderMap, string parameterArrayName,
        List<NameReference> names)
    {
        if (shaderMap is null)
            return;

        AddExpressions(shaderMap.UniformPixelScalarExpressions, parameterArrayName, names);
        AddExpressions(shaderMap.UniformPixelVectorExpressions, parameterArrayName, names);
        AddExpressions(shaderMap.UniformVertexScalarExpressions, parameterArrayName, names);
        AddExpressions(shaderMap.UniformVertexVectorExpressions, parameterArrayName, names);
        AddExpressions(shaderMap.Uniform2DTextureExpressions, parameterArrayName, names);
        AddExpressions(shaderMap.UniformCubeTextureExpressions, parameterArrayName, names);
    }

    internal static void AddExpressions(IEnumerable<MaterialUniformExpression> expressions,
        string parameterArrayName, List<NameReference> names)
    {
        if (expressions is null || !IsSupportedArray(parameterArrayName))
            return;

        var pending = new Stack<MaterialUniformExpression>(expressions.Where(expression => expression is not null));
        var visited = new HashSet<MaterialUniformExpression>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out MaterialUniformExpression expression))
        {
            if (!visited.Add(expression))
                continue;

            NameReference? name = expression switch
            {
                MaterialUniformExpressionScalarParameter parameter when parameterArrayName == "ScalarParameterValues" => parameter.ParameterName,
                MaterialUniformExpressionVectorParameter parameter when parameterArrayName == "VectorParameterValues" => parameter.ParameterName,
                MaterialUniformExpressionTextureParameter parameter when parameterArrayName == "TextureParameterValues" => parameter.ParameterName,
                _ => null
            };
            if (name is { } parameterName && !string.IsNullOrWhiteSpace(parameterName.Name)
                && !names.Any(existing => existing.Equals(parameterName)))
                names.Add(parameterName);

            switch (expression)
            {
                case MaterialUniformExpressionUnaryOp unary:
                    Push(unary.X);
                    break;
                case MaterialUniformExpressionBinaryOp binary:
                    Push(binary.A);
                    Push(binary.B);
                    break;
                case MaterialUniformExpressionClamp clamp:
                    Push(clamp.Input);
                    Push(clamp.Min);
                    Push(clamp.Max);
                    break;
            }
        }

        void Push(MaterialUniformExpression expression)
        {
            if (expression is not null)
                pending.Push(expression);
        }
    }
}
