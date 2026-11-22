using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Shaders.Recovery;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Misc;

/// <summary>
/// Opt-in, read-only audit of a ported level (LEX_LIVE_AUDIT=ported.pcc|source.pcc): each material in the ported file, whether it has
/// shaders, the source-game object it was ported from (found from the source level's point of view, so its BioP and overriding DLC count),
/// and which of its static mesh pixel shaders compute something different from the source's.
/// </summary>
[TestClass]
public class LiveLevelAuditTests
{
    [TestMethod]
    public void AuditPortedLevelMaterials()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_LIVE_AUDIT");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_LIVE_AUDIT.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        using var ported = MEPackageHandler.OpenMEPackage(parts[0], forceLoadFromDisk: true);
        using var source = MEPackageHandler.OpenMEPackage(parts[1], forceLoadFromDisk: true);
        var cache = new PackageCache();
        var carrier = source.Imports.First();
        var portedCacheExport = ported.FindExport("SeekFreeShaderCache");
        var portedCache = portedCacheExport == null ? null : ObjectBinary.From<ShaderCache>(portedCacheExport);
        var materials = ported.Exports.Where(e => !e.IsDefaultObject && (e.ClassName is "Material" or "DecalMaterial" ||
                                                  e.IsA("MaterialInstance") && e.GetProperty<BoolProperty>("bHasStaticPermutationResource")?.Value == true)).ToList();
        int broken = 0, unsourced = 0, different = 0, exact = 0, sameGame = 0;
        foreach (var material in materials)
        {
            string name = material.InstancedFullPath;
            if (ShaderCacheManipulator.IsMaterialBroken(material)) { broken++; Console.WriteLine($"BROKEN (no shaders): {name}"); continue; }
            var from = source.FindExport(name) ?? EntryImporter.ResolveImport(carrier, cache, instancedFullPathOverride: name);
            // Static lighting: a material without lightmap shaders leaves a lightmapped mesh with no baked light
            bool lightmapShaders = LegendaryExplorer.Tools.MaterialShaderCompilation.LE3MaterialRecompiler.HasLightmapShaders(material);
            var baseMaterial = material;
            while (baseMaterial.ClassName is not ("Material" or "DecalMaterial") && baseMaterial.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(ported) is ExportEntry parent) baseMaterial = parent;
            string lighting = $"lightmap shaders {lightmapShaders}, bUsedWithStaticLighting {baseMaterial.GetProperty<BoolProperty>("bUsedWithStaticLighting")?.Value.ToString() ?? "unset"} ({baseMaterial.ObjectName.Instanced})";
            if (from == null) { unsourced++; Console.WriteLine($"no {source.Game} source found: {name} [{material.ClassName}] {lighting}"); continue; }
            if (from.Game == ported.Game) { sameGame++; continue; }
            if (!lightmapShaders) Console.WriteLine($"NO LIGHTMAP SHADERS (from {source.Game}): {name} {lighting}");
            var (ours, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(ported, ShaderCacheManipulator.GetShaderMapId(material), null, true, []);
            var (theirs, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(from.FileRef, ShaderCacheManipulator.GetShaderMapId(from), null, true, []);
            var ourMesh = ours?.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == "FLocalVertexFactory");
            var theirMesh = theirs?.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == "FLocalVertexFactory");
            if (ourMesh == null || theirMesh == null) { Console.WriteLine($"no static mesh shaders to compare: {name} (ours {ourMesh != null}, theirs {theirMesh != null})"); continue; }
            var sourceCacheExport = from.FileRef.FindExport("SeekFreeShaderCache");
            var sourceCache = sourceCacheExport == null ? null : ObjectBinary.From<ShaderCache>(sourceCacheExport);
            var diffs = new List<string>();
            foreach (var (key, reference) in ourMesh.Shaders.Where(s => s.Key.Name.Contains("PixelShader")))
            {
                var theirRef = theirMesh.Shaders.FirstOrDefault(s => s.Key.Name == key.Name).Value;
                if (theirRef == null) continue;
                byte[] theirBytes = sourceCache != null && sourceCache.Shaders.TryGetValue(theirRef.Id, out var t) ? t.ShaderByteCode
                    : RefShaderCacheReader.GetShaders(from.Game, [theirRef.Id], out _, out _)?[0]?.ShaderByteCode;
                byte[] ourBytes = portedCache != null && portedCache.Shaders.TryGetValue(reference.Id, out var o) ? o.ShaderByteCode
                    : RefShaderCacheReader.GetShaders(ported.Game, [reference.Id], out _, out _)?[0]?.ShaderByteCode;
                if (theirBytes == null || ourBytes == null) continue;
                try
                {
                    var result = ShaderEquivalence.Compare(theirBytes, ourBytes, trials: 24);
                    if (!result.Equivalent) diffs.Add($"{key.Name}: {result}");
                }
                catch (ShaderRecoveryException) { } // loops: can't be compared
            }
            if (diffs.Count == 0) { exact++; continue; }
            different++;
            Console.WriteLine($"DIFFERS from {Path.GetFileName(from.FileRef.FilePath)}: {name} [{from.ClassName}]");
            foreach (string d in diffs.Take(3)) Console.WriteLine($"    {d}");
        }
        Console.WriteLine($"{Path.GetFileName(parts[0])}: {materials.Count} materials; {exact} match {source.Game}, {different} differ, {broken} have no shaders, " +
                          $"{unsourced} have no {source.Game} source, {sameGame} came from {ported.Game} (donors)");
    }
}
