using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.UserControls.ExportLoaderControls.MaterialEditor;

/// <summary>
/// Reads the parameters a material defines without creating texture previews or changing its package.
/// </summary>
internal static class MaterialParameterCatalog
{
    internal static IReadOnlyList<NameReference> GetTextureChoices(ExportEntry material,
        IEnumerable<NameReference> gameNames, PackageCache cache)
    {
        var names = gameNames.Concat(GetPackageTextureNames(material.FileRef)).ToList();
        try
        {
            names.AddRange(GetNames(material, "TextureParameterValues", cache));
        }
        catch (InvalidOperationException)
        {
            // A missing parent must not hide the game-wide catalog or parameters observed in this PCC.
        }

        return names.Where(name => !string.IsNullOrWhiteSpace(name.Name))
            .GroupBy(name => (name.Name.ToUpperInvariant(), name.Number))
            .Select(group => group.First())
            .OrderBy(name => name.Instanced, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name.Number).ToArray();
    }

    internal static IReadOnlyList<NameReference> GetPackageTextureNames(IMEPackage package) =>
        GameTextureParameterCatalog.GetNames(package);

    internal static IReadOnlyList<NameReference> GetNames(ExportEntry material, string parameterArrayName,
        PackageCache cache) => GetNames(material, parameterArrayName,
        entry => entry is ImportEntry import ? EntryImporter.ResolveImport(import, cache) : entry as ExportEntry,
        cache);

    internal static IReadOnlyList<NameReference> GetNames(ExportEntry material, string parameterArrayName,
        Func<IEntry, ExportEntry> resolver, PackageCache cache = null)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(resolver);
        if (parameterArrayName is not ("ScalarParameterValues" or "VectorParameterValues" or "TextureParameterValues"))
            throw new ArgumentException("Unknown material parameter array.", nameof(parameterArrayName));

        var names = new List<NameReference>();
        var seenNames = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<(IMEPackage Package, int Index)>();
        ExportEntry current = material;
        while (true)
        {
            if (!visited.Add((current.FileRef, current.UIndex)))
                throw new InvalidOperationException($"The material parent hierarchy contains a cycle at {current.InstancedFullPath}.");

            foreach (var name in CompiledMaterialParameterNames.GetNames(current, parameterArrayName, cache, material.FileRef))
                AddName(name);

            if (current.IsA("Material"))
            {
                var expressions = current.GetProperty<ArrayProperty<ObjectProperty>>("Expressions");
                if (expressions != null)
                {
                    foreach (var reference in expressions)
                    {
                        if (reference.Value == 0)
                            continue;

                        // Imported expressions carry their class in the import table. Resolve only the
                        // requested kind, so an unavailable unrelated node cannot hide valid parameters.
                        if (current.FileRef.GetEntry(reference.Value) is ImportEntry importedExpression
                            && !MatchesParameterType(importedExpression, parameterArrayName))
                            continue;

                        var expression = ResolveReference(current, reference.Value, "material expression", resolver);
                        if (MatchesParameterType(expression, parameterArrayName))
                            AddName(expression.GetProperty<NameProperty>("ParameterName")?.Value ?? NameReference.None);
                    }
                }

                return names.OrderBy(name => name.Instanced, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(name => name.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(name => name.Number).ToArray();
            }

            if (!current.IsA("MaterialInstance") && current.ClassName != "RvrEffectsMaterialUser")
                throw new InvalidOperationException($"{current.InstancedFullPath} is not a material or material instance.");

            var parent = current.GetProperty<ObjectProperty>("Parent");
            if ((parent == null || parent.Value == 0) && current.ClassName == "RvrEffectsMaterialUser")
                parent = current.GetProperty<ObjectProperty>("m_pBaseMaterial");
            if (parent == null || parent.Value == 0)
                throw new InvalidOperationException($"{current.InstancedFullPath} has no parent material from which to read available parameters.");

            current = ResolveReference(current, parent.Value, "parent material", resolver);
        }

        void AddName(NameReference name)
        {
            if (string.IsNullOrWhiteSpace(name.Name))
                return;
            if (!seenNames.TryGetValue(name.Name, out var numbers))
                seenNames.Add(name.Name, numbers = new HashSet<int>());
            if (numbers.Add(name.Number))
                names.Add(name);
        }
    }

    private static bool MatchesParameterType(IEntry expression, string parameterArrayName) => parameterArrayName switch
    {
        "ScalarParameterValues" => expression.IsA("MaterialExpressionScalarParameter"),
        "VectorParameterValues" => expression.IsA("MaterialExpressionVectorParameter"),
        "TextureParameterValues" => expression.IsA("MaterialExpressionTextureSampleParameter")
                                    || expression.IsA("MaterialExpressionTextureObjectParameter"),
        _ => false
    };

    private static ExportEntry ResolveReference(ExportEntry owner, int index, string description,
        Func<IEntry, ExportEntry> resolver)
    {
        var entry = owner.FileRef.GetEntry(index);
        if (entry is ExportEntry export)
            return export;
        if (entry is ImportEntry import)
        {
            var resolved = resolver(import);
            if (resolved != null)
                return resolved;
            throw new InvalidOperationException($"Could not resolve the imported {description} {import.InstancedFullPath} in {owner.FileRef.FilePath}.");
        }

        throw new InvalidOperationException($"The {description} reference {index} in {owner.InstancedFullPath} is invalid.");
    }
}
