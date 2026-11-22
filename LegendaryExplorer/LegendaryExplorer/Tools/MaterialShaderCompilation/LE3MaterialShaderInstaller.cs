using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.Tools.MaterialShaderCompilation
{
    /// <summary>
    /// Installs a compiled LE3 shader set into the package that owns the requested material, the way a cooked LE3 package
    /// carries it: the shader map and its shaders go in the package's SeekFreeShaderCache under the map's static key, and the
    /// material's SM3 resource (a base Material's SM3MaterialResource, or an instance's SM3StaticParameterSet and
    /// SM3StaticPermutationResource) is rebuilt from the same translation under the map's ID.
    /// </summary>
    /// <remarks>
    /// Callers give every recompiled material a fresh resource ID, so the new map can never collide with a map the game
    /// already has under the old key (in this package or the shipping RefShaderCache). A local map left under the old key is
    /// removed when nothing else in the package uses that key.
    /// </remarks>
    public sealed class LE3MaterialShaderInstaller : IMaterialShaderFragmentInstaller
    {
        public void Install(MaterialShaderCompileResult result, IMEPackage targetPackage)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(targetPackage);
            if (result.Outcome != MaterialShaderCompileOutcome.SucceededFragment || result.Fragment is not { Success: true } fragment)
                throw new InvalidOperationException("Only a successfully compiled shader set can be installed.");
            if (!fragment.IsCompleteShaderSet)
                throw new InvalidOperationException("The compiled shader set doesn't cover every shader the game requires for this material.");
            if (fragment.Cache.MaterialShaderMaps.Count != 1)
                throw new InvalidOperationException("A compiled fragment must hold exactly one material shader map.");
            var translation = result.Translation ?? throw new InvalidOperationException("The compile result has no translation.");
            var map = fragment.Cache.MaterialShaderMaps.Values.Single();
            Guid id = map.ID;

            ExportEntry owner = result.InstanceResolution?.PermutationOwner ?? translation.CompilationInput.BaseMaterial;
            if (!ReferenceEquals(owner.FileRef, targetPackage))
                throw new InvalidOperationException($"{owner.InstancedFullPath} is not in {targetPackage.FilePath}; only a package's own materials can be recompiled into it.");

            StaticParameterSet oldKey = ShaderCacheManipulator.GetShaderMapId(owner);

            // Textures: the translation's are UIndexes in the base Material's package (recovered code: in the owner's).
            IReadOnlyList<int> textures = MapTextures(translation, targetPackage);
            if (result.InstanceResolution?.PermutationOwner is { } instance)
            {
                var instanceTextures = LE3MaterialResourceBuilder.GetInstanceTextureParameterOrder(translation)
                    .Select(name => EffectiveTextureParameter(result.InstanceResolution.Chain, name, targetPackage)).ToArray();
                var resource = LE3MaterialResourceBuilder.Build(translation, id, textures, instanceTextures);
                var binary = ObjectBinary.From<MaterialInstance>(instance);
                binary.SM3StaticParameterSet = map.StaticParameters;
                binary.SM3StaticPermutationResource = resource;
                instance.WriteBinary(binary);
            }
            else
            {
                var resource = LE3MaterialResourceBuilder.Build(translation, id, textures);
                var binary = ObjectBinary.From<Material>(owner);
                binary.SM3MaterialResource = resource;
                owner.WriteBinary(binary);
            }

            ShaderCacheManipulator.AddShadersToFile(targetPackage, fragment.Cache, overwrite: true);
            RemoveUnusedLocalMap(targetPackage, oldKey, map.StaticParameters);
        }

        private static IReadOnlyList<int> MapTextures(LE3MaterialShaderCompiler.TranslationResult translation, IMEPackage targetPackage)
        {
            if (ReferenceEquals(translation.CompilationInput.BaseMaterial.FileRef, targetPackage)) return translation.UniformExpressionTextures;
            // Recovered code's textures are the recompiled material's own cooked list: already UIndexes in its (the target) package
            if (translation.IsRecovered) return translation.UniformExpressionTextures;
            // The base Material lives in another package (an instance of an imported parent): refer to the same textures
            // from the instance's package, adding imports where it has none.
            var source = translation.CompilationInput.BaseMaterial.FileRef;
            return LE3MaterialResourceBuilder.GetUniformExpressionTexturePaths(translation)
                .Select(path => (targetPackage.FindEntry(path) ??
                                 EntryImporter.GetOrAddCrossImportOrPackage(path, source, targetPackage, new RelinkerOptionsPackage()))?.UIndex
                                ?? throw new InvalidOperationException($"Cannot reference texture {path} from {targetPackage.FilePath}."))
                .ToArray();
        }

        /// <summary>The nearest TextureParameterValues override of <paramref name="name"/> along the instance chain, as a UIndex in
        /// <paramref name="targetPackage"/>; 0 when no instance overrides it.</summary>
        private static int EffectiveTextureParameter(IReadOnlyList<ExportEntry> chain, string name, IMEPackage targetPackage)
        {
            foreach (var link in chain)
            {
                if (link.IsA("Material")) break;
                foreach (var value in (IEnumerable<StructProperty>)link.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues") ?? [])
                {
                    if (!string.Equals(value.GetProp<NameProperty>("ParameterName")?.Value.Instanced, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (value.GetProp<ObjectProperty>("ParameterValue")?.ResolveToEntry(link.FileRef) is not { } texture) return 0;
                    if (ReferenceEquals(link.FileRef, targetPackage)) return texture.UIndex;
                    return (targetPackage.FindEntry(texture.InstancedFullPath) ??
                            EntryImporter.GetOrAddCrossImportOrPackage(texture.InstancedFullPath, link.FileRef, targetPackage, new RelinkerOptionsPackage()))?.UIndex ?? 0;
                }
            }
            return 0;
        }

        private static void RemoveUnusedLocalMap(IMEPackage package, StaticParameterSet oldKey, StaticParameterSet newKey)
        {
            if (oldKey == null || oldKey.Equals(newKey) || package.FindExport("SeekFreeShaderCache", "ShaderCache") is not { } cacheExport) return;
            var cache = ObjectBinary.From<ShaderCache>(cacheExport);
            if (!cache.MaterialShaderMaps.TryGetValue(oldKey, out var oldMap)) return;
            foreach (var export in package.Exports)
            {
                if (export.IsDefaultObject) continue;
                bool usesKey = export.ClassName is "Material" or "DecalMaterial" ||
                               export.IsA("MaterialInstance") && export.GetProperty<BoolProperty>("bHasStaticPermutationResource")?.Value == true;
                if (usesKey && oldKey.Equals(ShaderCacheManipulator.GetShaderMapId(export))) return;
            }
            cache.MaterialShaderMaps.Remove(oldKey);
            // Drop the old map's shaders unless another map still points at them.
            var stillUsed = cache.MaterialShaderMaps.Values.SelectMany(ShaderIds).ToHashSet();
            foreach (var shaderId in ShaderIds(oldMap).Where(s => !stillUsed.Contains(s)).ToList())
                cache.Shaders.Remove(shaderId);
            cacheExport.WriteBinary(cache);

            static IEnumerable<Guid> ShaderIds(MaterialShaderMap m) =>
                m.Shaders.Select(s => s.Value.Id).Concat(m.MeshShaderMaps.SelectMany(mesh => mesh.Shaders.Select(s => s.Value.Id)));
        }
    }
}
