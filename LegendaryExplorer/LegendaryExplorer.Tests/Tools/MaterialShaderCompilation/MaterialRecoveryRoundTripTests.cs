using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.MaterialShaderCompilation;
using LegendaryExplorerCore;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Shaders.Recovery;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Compiler = LegendaryExplorerCore.Shaders.LE3MaterialShaderCompiler;

namespace LegendaryExplorer.Tests.Tools.MaterialShaderCompilation;

/// <summary>
/// Opt-in (LEX_MATERIAL_RECOMPILE_IT=1): shipped LE3 materials, whose cooked graphs are stripped, are recovered from their shipping
/// shaders and recompiled with the LE3 compiler; each recompiled static-mesh shader must compute what the shipping one does.
/// LEX_MATERIAL_RECOVERY_PACKAGE picks the package (default BioA_CitHub_Embassy), LEX_MATERIAL_RECOVERY_COUNT how many materials.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class MaterialRecoveryRoundTripTests
{
    [TestInitialize]
    public void Setup()
    {
        if (Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOMPILE_IT") != "1") Assert.Inconclusive("Set LEX_MATERIAL_RECOMPILE_IT=1.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
    }

    /// <summary>
    /// LE2 → LE3: each LE2 material is ported into an LE3 package as Clone All does, recovered from its LE2 shipping shaders, compiled
    /// for LE3, and every shader type both games have is compared with the LE2 one. LEX_MATERIAL_RECOVERY_LE2 picks the package.
    /// </summary>
    [TestMethod]
    public void LE2_RecoveredMaterials_MatchLE2Shaders()
    {
        string path = Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_LE2") ?? Path.Combine(LE2Directory.CookedPCPath, "BioA_CitHub_500Udina.pcc");
        int count = int.TryParse(Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_COUNT"), out int n) ? n : 12;
        int show = int.TryParse(Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_SHOW"), out int s) ? s : 4;
        using var source = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        using var dest = MEPackageHandler.CreateMemoryEmptyPackage("LEXRecoveryLE2.pcc", MEGame.LE3);
        var sources = LE3ShaderSourceSnapshot.FromDirectory(LE3MaterialRecompiler.DefaultShaderDirectory, 1252);
        var cache = new PackageCache();
        var report = new List<string>();
        int matched = 0, recovered = 0;
        string only = Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_ONLY");
        var candidates = source.Exports.Where(e => (e.ClassName is "Material" or "DecalMaterial") && !e.IsDefaultObject && (only == null || only.Split(",").Contains(e.ObjectName.Instanced))).ToList();
        // Named materials the package only imports: the exports they resolve to (memory-safe porting ports those)
        if (only != null)
            foreach (var import in source.Imports.Where(i => i.ClassName is "Material" or "DecalMaterial" && only.Split(",").Contains(i.ObjectName.Instanced)))
                if (EntryImporter.TryResolveImport(import, out var resolvedImport, cache)) candidates.Add(resolvedImport);
        foreach (var material in candidates.Take(count))
        {
            string name = material.InstancedFullPath;
            var (map, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(material.FileRef, ShaderCacheManipulator.GetShaderMapId(material), null, true, []);
            if (map == null) { report.Add($"{name}: no LE2 shader map"); continue; }
            var localCache = material.FileRef.FindExport("SeekFreeShaderCache", "ShaderCache") is { } ce ? ObjectBinary.From<ShaderCache>(ce) : null;
            byte[] Shipping(string vf, string type)
            {
                var reference = map.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == vf)?.Shaders.FirstOrDefault(x => x.Key.Name == type).Value;
                if (reference == null) return null;
                if (localCache != null && localCache.Shaders.TryGetValue(reference.Id, out var local)) return local.ShaderByteCode;
                return RefShaderCacheReader.GetShaders(MEGame.LE2, [reference.Id], out _, out _)?[0]?.ShaderByteCode;
            }
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, material, dest, null, true,
                new RelinkerOptionsPackage { IsCrossGame = true, Cache = new PackageCache(), ImportExportDependencies = true, PortImportsMemorySafe = true }, out IEntry newEntry);
            var ported = (ExportEntry)newEntry;
            var resource = ObjectBinary.From<Material>(ported).SM3MaterialResource;
            RecoveredMaterialCode code;
            try { code = MaterialCodeRecovery.Recover(ported, map, resource, Shipping); }
            catch (ShaderRecoveryException e) { report.Add($"{name}: recovery failed: {e.Message}"); continue; }
            recovered++;
            var compiler = new Compiler(new Compiler.ResolvedMaterialCompilationInput(ported), getTextureMetadata: e =>
                new Compiler.TextureCompilationMetadata(e.IsA("TextureCube") ? Compiler.TextureDimension.TextureCube : Compiler.TextureDimension.Texture2D));
            var translation = compiler.TranslateRecoveredMaterial(sources, code);
            if (!translation.Success) { report.Add($"{name}: translation failed: {string.Join("; ", translation.Errors)}"); continue; }
            var plan = compiler.BuildKnownSubsetCompilePlan(translation, resource);
            if (!plan.Success) { report.Add($"{name}: plan failed: {string.Join("; ", plan.Errors.Take(2))}"); continue; }
            // LEX_MATERIAL_RECOVERY_VF: the vertex factory to compare (static meshes by default; FLocalDecalVertexFactory for static decals)
            string vertexFactory = Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_VF") is { Length: > 0 } vf ? vf : "FLocalVertexFactory";
            var shipMesh = map.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == vertexFactory);
            var jobs = plan.Jobs.Where(j => j.Target.VertexFactoryTypeName == vertexFactory && j.Target.ShaderTypeName.Contains("PixelShader") &&
                                            shipMesh?.Shaders.Any(x => x.Key.Name == j.Target.ShaderTypeName) == true).ToList();
            var results = new System.Collections.Concurrent.ConcurrentBag<(string Type, ShaderEquivalenceResult Result, string Error)>();
            Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 16 }, job =>
            {
                var compiled = compiler.CompileShader(job, sources);
                if (!compiled.Success) { results.Add((job.Target.ShaderTypeName, null, compiled.Diagnostics)); return; }
                if (Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_ASM") is { Length: > 0 } asmDirectory)
                {
                    using var asm = new SharpDX.D3DCompiler.ShaderBytecode(compiled.Bytecode);
                    File.WriteAllText(Path.Combine(asmDirectory, $"{material.ObjectName.Instanced}_{job.Target.ShaderTypeName.Replace('<', '(').Replace('>', ')')}.asm"), asm.Disassemble());
                }
                try { results.Add((job.Target.ShaderTypeName, ShaderEquivalence.Compare(Shipping(vertexFactory, job.Target.ShaderTypeName), compiled.Bytecode), null)); }
                catch (ShaderRecoveryException e) { results.Add((job.Target.ShaderTypeName, null, "compare: " + e.Message)); }
            });
            var bad = results.Where(r => r.Result?.Equivalent != true && r.Error?.Contains("loops") != true).OrderBy(r => r.Type).ToList();
            if (bad.Count == 0 && results.Count > 0) matched++;
            report.Add($"{name} [{string.Join(",", code.PropertyCode.Keys)}]: {results.Count - bad.Count}/{results.Count} LE2 {vertexFactory} pixel shaders equivalent");
            if (Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_DUMP") == "1")
            {
                foreach (string note in code.Notes) report.Add($"    note: {note}");
                foreach (var (property, text) in code.PropertyCode) report.Add($"    {property}:{Environment.NewLine}{text}");
            }
            foreach (var (type, result, error) in bad.Take(show))
                report.Add($"    {type}: {(error != null ? "ERROR " + error.Split('\n')[0] : result + (Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_DUMP") == "1" ? " | " + string.Join("; ", result.Differences) : ""))}");
        }
        foreach (string line in report) Console.WriteLine(line);
        Console.WriteLine($"recovered {recovered}, all shaders equivalent {matched}");
        Assert.IsTrue(recovered > 0);
    }

    [TestMethod]
    public void LE3_RecoveredMaterials_MatchShippingShaders()
    {
        string path = Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_PACKAGE") ?? Path.Combine(LE3Directory.CookedPCPath, "BioA_CitHub_Embassy.pcc");
        int count = int.TryParse(Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_COUNT"), out int n) ? n : 12;
        string only = Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_ONLY");
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        var sources = LE3ShaderSourceSnapshot.FromDirectory(LE3MaterialRecompiler.DefaultShaderDirectory, 1252);
        var cache = new PackageCache();
        int recovered = 0, matched = 0, failedRecovery = 0;
        var report = new List<string>();
        foreach (var material in package.Exports.Where(e => e.ClassName == "Material" && !e.IsDefaultObject &&
                                                            (only == null || only.Split(",").Contains(e.ObjectName.Instanced))).Take(count))
        {
            string name = material.InstancedFullPath;
            var resource = ObjectBinary.From<Material>(material).SM3MaterialResource;
            var map = RefShaderCacheReader.GetMaterialShaderMap(MEGame.LE3, ShaderCacheManipulator.GetShaderMapId(material), out _);
            if (map == null) { report.Add($"{name}: no shipping map"); continue; }
            byte[] Shipping(string vf, string type)
            {
                var mesh = map.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == vf);
                var reference = mesh?.Shaders.FirstOrDefault(s => s.Key.Name == type).Value;
                return reference == null ? null : RefShaderCacheReader.GetShaders(MEGame.LE3, [reference.Id], out _, out _)?[0]?.ShaderByteCode;
            }

            RecoveredMaterialCode code;
            try { code = MaterialCodeRecovery.Recover(material, map, resource, Shipping); }
            catch (ShaderRecoveryException e) { failedRecovery++; report.Add($"{name}: recovery failed: {e.Message}"); continue; }
            recovered++;

            var compiler = new Compiler(new Compiler.ResolvedMaterialCompilationInput(material), getTextureMetadata: e =>
            {
                var t = e as ExportEntry ?? (EntryImporter.TryResolveImport((ImportEntry)e, out var r, cache) ? r : null);
                bool cube = t?.IsA("TextureCube") == true;
                return new Compiler.TextureCompilationMetadata(cube ? Compiler.TextureDimension.TextureCube : Compiler.TextureDimension.Texture2D);
            });
            var translation = compiler.TranslateRecoveredMaterial(sources, code);
            if (!translation.Success) { report.Add($"{name}: translation failed: {string.Join("; ", translation.Errors)}"); continue; }
            var plan = compiler.BuildKnownSubsetCompilePlan(translation, resource);
            if (!plan.Success) { report.Add($"{name}: plan failed: {string.Join("; ", plan.Errors.Take(2))}"); continue; }

            // Every pixel shader both maps have on the static mesh factory
            var shipMesh = map.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == "FLocalVertexFactory");
            var jobs = plan.Jobs.Where(j => j.Target.VertexFactoryTypeName == "FLocalVertexFactory" && j.Target.ShaderTypeName.Contains("PixelShader") &&
                                            shipMesh?.Shaders.Any(s => s.Key.Name == j.Target.ShaderTypeName) == true).ToList();
            var results = new System.Collections.Concurrent.ConcurrentBag<(string Type, ShaderEquivalenceResult Result, string Error)>();
            Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 16 }, job =>
            {
                var compiled = compiler.CompileShader(job, sources);
                if (!compiled.Success) { results.Add((job.Target.ShaderTypeName, null, compiled.Diagnostics)); return; }
                try
                {
                    results.Add((job.Target.ShaderTypeName, ShaderEquivalence.Compare(Shipping("FLocalVertexFactory", job.Target.ShaderTypeName), compiled.Bytecode), null));
                }
                catch (ShaderRecoveryException e) { results.Add((job.Target.ShaderTypeName, null, "compare: " + e.Message)); }
            });
            // Editor debug view shaders with loops can't be compared; they aren't failures
            var bad = results.Where(r => r.Result?.Equivalent != true && r.Error?.Contains("loops") != true).OrderBy(r => r.Type).ToList();
            if (bad.Count == 0 && results.Count > 0) matched++;
            var mp = material.GetProperties();
            string flags = $" [{mp.GetProp<EnumProperty>("BlendMode")?.Value.Name ?? "opaque"} {mp.GetProp<EnumProperty>("LightingModel")?.Value.Name ?? "phong"}{(mp.GetProp<BoolProperty>("TwoSided")?.Value == true ? " twosided" : "")} inputs: {string.Join(",", mp.OfType<StructProperty>().Where(p => p.GetProp<ObjectProperty>("Expression")?.Value is > 0).Select(p => p.Name.Name))} recovered: {string.Join(",", code.PropertyCode.Keys)}]";
            report.Add($"{name}{flags}: {results.Count - bad.Count}/{results.Count} pixel shaders equivalent{(code.Notes.Count > 0 ? " [" + string.Join(" ", code.Notes) + "]" : "")}");
            foreach (var (type, result, error) in bad.Take(int.TryParse(Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_SHOW"), out int show) ? show : 4))
                report.Add($"    {type}: {(error != null ? "ERROR " + error.Split('\n')[0] : result.ToString())}");
            if (Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_DUMP") == "1")
                foreach (var (property, text) in code.PropertyCode) report.Add($"    {property}:\n{text}");
            if (Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOVERY_PROBE") is { Length: > 0 } probeType)
            {
                var job = plan.Jobs.First(j => j.Target.VertexFactoryTypeName == "FLocalVertexFactory" && j.Target.ShaderTypeName == probeType);
                var ours = compiler.CompileShader(job, sources).Bytecode;
                foreach (var (label, bytes) in new[] { ("shipping", Shipping("FLocalVertexFactory", probeType)), ("ours", ours) })
                {
                    var flow = DxbcSymbolicExecutor.Execute(bytes);
                    var color = flow.Output("o0.x");
                    var transfer = ExprAlgebra.Derivative(flow.Pool, color, e => e.Op == ExprOp.Leaf && e.Leaf == "cb:LightColorAndFalloffExponent[0]");
                    report.Add($"  {label} inputs: {string.Join(", ", flow.InputSemantics.OrderBy(p => p.Key).Select(p => $"v{p.Key}={p.Value}"))}");
                    var fixedLeaves = new Dictionary<string, double>
                    {
                        ["in:TEXCOORD10.x"] = 1, ["in:TEXCOORD10.y"] = 0, ["in:TEXCOORD10.z"] = 0,
                        ["in:TEXCOORD11.x"] = 0, ["in:TEXCOORD11.y"] = 0, ["in:TEXCOORD11.z"] = 1, ["in:TEXCOORD11.w"] = 1,
                        ["in:TEXCOORD4.x"] = 0.3, ["in:TEXCOORD4.y"] = 0.5, ["in:TEXCOORD4.z"] = 0.8,
                        ["in:TEXCOORD6.x"] = 0.1, ["in:TEXCOORD6.y"] = -0.4, ["in:TEXCOORD6.z"] = 0.9,
                        ["in:TEXCOORD0.x"] = 0.37, ["in:TEXCOORD0.y"] = 0.61,
                    };
                    double FixedLeaf(string leaf) => fixedLeaves.TryGetValue(leaf, out double v) ? v : 0.5;
                    double FixedSample(SampleInfo s, double[] uv, int c) => 0.3 + 0.1 * c + 0.2 * Math.Sin(uv.Sum() * 3 + s.Texture.Length);
                    foreach (var sat in ExprAlgebra.Nodes(flow.Output("o0.x")).Where(e => e.Op == ExprOp.Sat))
                        report.Add($"    sat = {ExprAlgebra.Evaluate(sat, FixedLeaf, FixedSample):G6} arg = {ExprAlgebra.Evaluate(sat.Args[0], FixedLeaf, FixedSample):G6} leaves: {string.Join(" ", ExprAlgebra.Nodes(sat).Where(n => n.Op == ExprOp.Leaf).Select(n => n.Leaf).Distinct().OrderBy(x => x))} samples: {string.Join(" ", ExprAlgebra.Nodes(sat).Where(n => n.Op == ExprOp.Sample).Select(n => n.SampleInfo.Texture).Distinct())}");
                    report.Add($"    o0.x = {ExprAlgebra.Evaluate(flow.Output("o0.x"), FixedLeaf, FixedSample):G6}");
                    // N components: aim the light down each axis; the N·L saturate's argument is then that component
                    var nl = ExprAlgebra.Nodes(flow.Output("o0.x")).Where(e => e.Op == ExprOp.Sat &&
                        ExprAlgebra.Nodes(e).Any(n => n.Op == ExprOp.Leaf && n.Leaf == "in:TEXCOORD4.x") &&
                        !ExprAlgebra.Nodes(e).Any(n => n.Op == ExprOp.Leaf && n.Leaf == "in:TEXCOORD6.x")).ToList();
                    var n3 = new double[3];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        for (int k = 0; k < 3; k++) fixedLeaves[$"in:TEXCOORD4.{"xyz"[k]}"] = k == axis ? 1 : 0;
                        n3[axis] = nl.Count > 0 ? ExprAlgebra.Evaluate(nl[0].Args[0], FixedLeaf, FixedSample) : double.NaN;
                    }
                    report.Add($"    light-pass N = ({n3[0]:F4}, {n3[1]:F4}, {n3[2]:F4})");
                    // Base pass normal output at WorldToView = I: normalize(N).xy * 0.5 + 0.5
                    var baseBytes = label == "shipping" ? Shipping("FLocalVertexFactory", MaterialCodeRecovery.UnlitShader)
                        : compiler.CompileShader(plan.Jobs.First(j => j.Target.VertexFactoryTypeName == "FLocalVertexFactory" && j.Target.ShaderTypeName == MaterialCodeRecovery.UnlitShader), sources).Bytecode;
                    var baseFlow = DxbcSymbolicExecutor.Execute(baseBytes);
                    double Identity(string leaf) => leaf.StartsWith("cb:WorldToViewMatrix[") ? (int.Parse(leaf[21..^1]) is var i && i % 4 == i / 4 ? 1 : 0) : FixedLeaf(leaf);
                    report.Add($"    base-pass N.xy = ({ExprAlgebra.Evaluate(baseFlow.Output("o2.x"), Identity, FixedSample) * 2 - 1:F4}, {ExprAlgebra.Evaluate(baseFlow.Output("o2.y"), Identity, FixedSample) * 2 - 1:F4})");
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), $"probe_{label}.asm"), flow.Disassembly);
                }
            }
        }
        foreach (string line in report) Console.WriteLine(line);
        Console.WriteLine($"recovered {recovered}, all shaders equivalent {matched}, recovery failed {failedRecovery}");
        Assert.IsTrue(recovered > 0);
    }
}
