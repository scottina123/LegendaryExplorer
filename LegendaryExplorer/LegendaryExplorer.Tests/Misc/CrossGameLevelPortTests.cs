using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using System.Collections.Generic;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Misc;

/// <summary>
/// Clone All of a whole LE2 level (TheWorld) into an empty LE3 package with the porting dialog's defaults: destination game donors and
/// memory-safe import porting. Opt-in (LEX_PORT_LEVEL_IT=1; LEX_PORT_LEVEL_SOURCE picks the LE2 level, LEX_PORT_LEVEL_DEST an LE3 file to
/// port into, opened and never saved).
/// </summary>
[TestClass]
public class CrossGameLevelPortTests
{
    [TestMethod]
    public void LE2LevelIntoEmptyLE3Package_PortsWithDonorsAndMemorySafeImports()
    {
        if (Environment.GetEnvironmentVariable("LEX_PORT_LEVEL_IT") != "1") Assert.Inconclusive("Set LEX_PORT_LEVEL_IT=1.");
        string sourcePath = Environment.GetEnvironmentVariable("LEX_PORT_LEVEL_SOURCE") ?? Path.Combine(LE2Directory.CookedPCPath ?? "", "BioA_TwrHub_200Lounge.pcc");
        if (!File.Exists(sourcePath)) Assert.Inconclusive($"{sourcePath} wasn't found.");
        string donorPath = AppDirectories.GetObjectDatabasePath(MEGame.LE3);
        if (!File.Exists(donorPath)) Assert.Inconclusive($"No LE3 object database at {donorPath}.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });

        ObjectInstanceDB donors;
        using (var stream = File.OpenRead(donorPath)) donors = ObjectInstanceDB.Deserialize(MEGame.LE3, stream);
        using var source = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
        string destPath = Environment.GetEnvironmentVariable("LEX_PORT_LEVEL_DEST");
        using var dest = destPath is { Length: > 0 }
            ? MEPackageHandler.OpenMEPackage(destPath, forceLoadFromDisk: true)
            : MEPackageHandler.CreateMemoryEmptyPackage(Path.GetFileName(sourcePath), MEGame.LE3);
        var world = source.FindExport("TheWorld");
        var rop = new RelinkerOptionsPackage
        {
            IsCrossGame = true, Cache = new PackageCache(), TargetGameDonorDB = donors, ImportExportDependencies = true, PortImportsMemorySafe = true,
        };
        var results = EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, world, dest, null, true, rop, out IEntry newEntry);
        foreach (var result in results.Take(20)) Console.WriteLine(result.Message);
        Console.WriteLine($"{results.Count} relink results; {dest.ExportCount} exports, {dest.ImportCount} imports");
        Assert.IsNotNull(newEntry);

        // Every object reference in a ported export's properties must point at an entry that exists
        int broken = 0;
        foreach (var export in dest.Exports)
        {
            foreach (var reference in export.GetProperties().SelectMany(Flatten).OfType<ObjectProperty>())
            {
                if (reference.Value == 0 || dest.IsEntry(reference.Value)) continue;
                if (broken++ < 10) Console.WriteLine($"{export.InstancedFullPath}: {reference.Name.Instanced} = {reference.Value}, which isn't an entry");
            }
        }
        Assert.AreEqual(0, broken, "Ported exports reference entries that don't exist.");

        // Every ported export knows what it was ported from, including the ones the level only imported (from its BioP, say)
        var sources = LegendaryExplorer.Tools.MaterialShaderCompilation.LE3MaterialRecompiler.PortedSources(rop);
        var unsourced = dest.Exports.Where(e => !e.IsDefaultObject && e.ClassName is not ("Package" or "ShaderCache") && !sources.ContainsKey(e) &&
                                                !e.InstancedFullPath.StartsWith("TheWorld", StringComparison.Ordinal)).ToList();
        foreach (var e in unsourced.Take(10)) Console.WriteLine($"no source: {e.InstancedFullPath} ({e.ClassName})");
        foreach (var (ported, from) in sources.Where(p => p.Value.FileRef != source).Take(8))
            Console.WriteLine($"from another file: {ported.InstancedFullPath} <- {Path.GetFileName(from.FileRef.FilePath)}");
        Console.WriteLine($"import files for {Path.GetFileName(sourcePath)}: {string.Join(", ", EntryImporter.GetPossibleImportFiles(source, source.Imports.First()))}");
        foreach (var import in rop.CrossPackageMap.Keys.OfType<ImportEntry>().Where(i => i.InstancedFullPath.StartsWith("BIOG_APL_INT_Chair_Bar_02_L", StringComparison.OrdinalIgnoreCase)).Take(2))
            Console.WriteLine($"import {import.InstancedFullPath} is in {Path.GetFileName(import.FileRef.FilePath)}; its files: {string.Join(", ", EntryImporter.GetPossibleImportFiles(import))}");

        // LEX_PORT_LEVEL_RECOMPILE=name,name (or "all"): recompile those ported materials as Clone All does, and print each outcome
        if (Environment.GetEnvironmentVariable("LEX_PORT_LEVEL_RECOMPILE") is { Length: > 0 } recompileSpec &&
            LegendaryExplorer.Tools.MaterialShaderCompilation.LE3MaterialRecompiler.CanRecompile(MEGame.LE3, out _))
        {
            var names = recompileSpec.Split(',');
            var materials = dest.Exports.Where(e => (e.ClassName is "Material" or "DecalMaterial" ||
                                                     e.IsA("MaterialInstance") && e.GetProperty<BoolProperty>("bHasStaticPermutationResource")?.Value == true) &&
                                                    (recompileSpec == "all" || names.Contains(e.ObjectName.Instanced)) &&
                                                    LegendaryExplorer.Tools.MaterialShaderCompilation.LE3MaterialRecompiler.NeedsRecompileAfterPort(e, sources.GetValueOrDefault(e))).ToList();
            var outcomes = new LegendaryExplorer.Tools.MaterialShaderCompilation.LE3MaterialRecompiler()
                .Recompile(materials, _ => [], sourceOf: m => sources.GetValueOrDefault(m));
            foreach (var outcome in outcomes)
                Console.WriteLine($"recompile: {outcome} [from {sources.GetValueOrDefault(outcome.Material)?.FileRef.FilePath}|{sources.GetValueOrDefault(outcome.Material)?.InstancedFullPath}]");
            Console.WriteLine($"recompiled {outcomes.Count(o => o.Success)} of {outcomes.Count}");

            // Each recompiled material's installed static mesh pixel shaders against its source's, with what differs
            foreach (var outcome in outcomes.Where(o => o.Success && sources.ContainsKey(o.Material)))
            {
                var from = sources[outcome.Material];
                var (ourMap, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(dest, ShaderCacheManipulator.GetShaderMapId(outcome.Material), null, true, []);
                var (theirMap, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(from.FileRef, ShaderCacheManipulator.GetShaderMapId(from), null, true, []);
                var ourCache = LegendaryExplorerCore.Unreal.BinaryConverters.ObjectBinary.From<LegendaryExplorerCore.Unreal.BinaryConverters.ShaderCache>(dest.FindExport("SeekFreeShaderCache"));
                var theirCacheExport = from.FileRef.FindExport("SeekFreeShaderCache");
                var theirCache = theirCacheExport == null ? null : LegendaryExplorerCore.Unreal.BinaryConverters.ObjectBinary.From<LegendaryExplorerCore.Unreal.BinaryConverters.ShaderCache>(theirCacheExport);
                var ours = ourMap?.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == "FLocalVertexFactory");
                var theirs = theirMap?.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == "FLocalVertexFactory");
                if (ours == null || theirs == null) continue;
                foreach (var (type, reference) in ours.Shaders.Where(x => x.Key.Name.Contains("PixelShader")).Select(x => (x.Key.Name, x.Value)))
                {
                    var theirRef = theirs.Shaders.FirstOrDefault(x => x.Key.Name == type).Value;
                    if (theirRef == null) continue;
                    byte[] theirBytes = theirCache != null && theirCache.Shaders.TryGetValue(theirRef.Id, out var t) ? t.ShaderByteCode
                        : LegendaryExplorerCore.Shaders.RefShaderCacheReader.GetShaders(from.Game, [theirRef.Id], out _, out _)?[0]?.ShaderByteCode;
                    if (theirBytes == null || !ourCache.Shaders.TryGetValue(reference.Id, out var ourShader)) continue;
                    LegendaryExplorerCore.Shaders.Recovery.ShaderEquivalenceResult cmp;
                    try { cmp = LegendaryExplorerCore.Shaders.Recovery.ShaderEquivalence.Compare(theirBytes, ourShader.ShaderByteCode); }
                    catch (LegendaryExplorerCore.Shaders.Recovery.ShaderRecoveryException) { continue; } // loops: can't be compared
                    if (!cmp.Equivalent) Console.WriteLine($"differs {outcome.Material.ObjectName.Instanced} {type}: {cmp} | {string.Join("; ", cmp.Differences.Take(6))}");
                }
                if (Environment.GetEnvironmentVariable("LEX_PORT_LEVEL_PROBE_OUT") is { Length: > 0 } probeOut)
                {
                    foreach (string type in new[] { "TBasePassPixelShaderFDirectionalLightLightMapPolicyNoSkyLight", "TBasePassPixelShaderFNoLightMapPolicyNoSkyLight" })
                    {
                        var theirRef = theirs.Shaders.FirstOrDefault(x => x.Key.Name == type).Value;
                        var ourRef = ours.Shaders.FirstOrDefault(x => x.Key.Name == type).Value;
                        if (theirRef != null && theirCache != null && theirCache.Shaders.TryGetValue(theirRef.Id, out var ts))
                            File.WriteAllText(Path.Combine(probeOut, $"{outcome.Material.ObjectName.Instanced}_{type}_LE2.asm"), new SharpDX.D3DCompiler.ShaderBytecode(ts.ShaderByteCode).Disassemble());
                        if (ourRef != null && ourCache.Shaders.TryGetValue(ourRef.Id, out var os))
                            File.WriteAllText(Path.Combine(probeOut, $"{outcome.Material.ObjectName.Instanced}_{type}_LE3.asm"), new SharpDX.D3DCompiler.ShaderBytecode(os.ShaderByteCode).Disassemble());
                    }
                }
            }
        }
    }

    private static System.Collections.Generic.IEnumerable<Property> Flatten(Property p) => p switch
    {
        StructProperty s => s.Properties.SelectMany(Flatten).Prepend(p),
        ArrayProperty<StructProperty> a => a.SelectMany(x => Flatten(x)).Prepend(p),
        ArrayProperty<ObjectProperty> a => a.Cast<Property>().Prepend(p),
        _ => [p]
    };
}
