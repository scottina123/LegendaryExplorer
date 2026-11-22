using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.MaterialShaderCompilation;

/// <summary>Opt-in probe (LEX_SHADER_PROBE=package|materialPath|vertexFactory|shaderType): dumps a shipped shader's disassembly and reflection.</summary>
[TestClass]
public class ShaderRecoveryProbeTests
{
    [TestMethod]
    public void DumpShader()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_SHADER_PROBE");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_SHADER_PROBE.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        using var package = MEPackageHandler.OpenMEPackage(parts[0], forceLoadFromDisk: true);
        var material = package.FindExport(parts[1]);
        var (map, shaders) = ShaderCacheManipulator.GetMaterialShaderMapAndShadersForVertexFactory(material, parts[2], parts[3]);
        var shader = shaders[0];
        Assert.IsNotNull(shader, "shader not found");
        using var bytecode = new SharpDX.D3DCompiler.ShaderBytecode(shader.ShaderByteCode);
        string outDir = Environment.GetEnvironmentVariable("LEX_SHADER_PROBE_OUT") ?? Path.GetTempPath();
        File.WriteAllText(Path.Combine(outDir, "probe.asm"), bytecode.Disassemble());
        using var reflection = new SharpDX.D3DCompiler.ShaderReflection(shader.ShaderByteCode);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < reflection.Description.InputParameters; i++)
        {
            var p = reflection.GetInputParameterDescription(i);
            sb.AppendLine($"in v{p.Register} {p.SemanticName}{p.SemanticIndex} mask {p.UsageMask}");
        }
        for (int i = 0; i < reflection.Description.OutputParameters; i++)
        {
            var p = reflection.GetOutputParameterDescription(i);
            sb.AppendLine($"out o{p.Register} {p.SemanticName}{p.SemanticIndex}");
        }
        for (int i = 0; i < reflection.Description.BoundResources; i++)
        {
            var r = reflection.GetResourceBindingDescription(i);
            sb.AppendLine($"res {r.Type} {r.Name} slot {r.BindPoint}");
        }
        for (int c = 0; c < reflection.Description.ConstantBuffers; c++)
        {
            var cb = reflection.GetConstantBuffer(c);
            sb.AppendLine($"cbuffer {cb.Description.Name}");
            for (int v = 0; v < cb.Description.VariableCount; v++)
            {
                var variable = cb.GetVariable(v);
                sb.AppendLine($"  {variable.Description.Name} offset {variable.Description.StartOffset} size {variable.Description.Size} {variable.GetVariableType().Description.Class} {variable.GetVariableType().Description.RowCount}x{variable.GetVariableType().Description.ColumnCount}");
            }
        }
        sb.AppendLine("uniform pixel vectors: " + string.Join(" | ", map.UniformPixelVectorExpressions.Select(e => e.GetType().Name)));
        sb.AppendLine("uniform pixel scalars: " + string.Join(" | ", map.UniformPixelScalarExpressions.Select(e => e.GetType().Name)));
        sb.AppendLine("uniform 2D: " + string.Join(" | ", map.Uniform2DTextureExpressions.Select(e => e.GetType().Name + "#" + e.TextureIndex)));
        sb.AppendLine("uniform cube: " + string.Join(" | ", map.UniformCubeTextureExpressions.Select(e => e.GetType().Name + "#" + e.TextureIndex)));
        File.WriteAllText(Path.Combine(outDir, "probe.txt"), sb.ToString());
    }
}

/// <summary>Opt-in probe (LEX_MATERIAL_PROBE=package|instancedFullPath): a material's parent chain and its resource's texture list.</summary>
[TestClass]
public class MaterialReferenceProbeTests
{
    [TestMethod]
    public void DumpMaterialReferences()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_MATERIAL_PROBE");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_MATERIAL_PROBE.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        using var package = MEPackageHandler.OpenMEPackage(parts[0], forceLoadFromDisk: true);
        var material = package.FindExport(parts[1]) ?? package.Exports.FirstOrDefault(e => e.ObjectName.Instanced == parts[1]);
        Assert.IsNotNull(material, "material not found");
        IEntry link = material;
        while (link is ExportEntry export && export.GetProperty<ObjectProperty>("Parent") is { } parent)
        {
            var next = package.GetEntry(parent.Value);
            Console.WriteLine($"{link.InstancedFullPath} -> Parent {parent.Value} {next?.InstancedFullPath} ({next?.ClassName}, {(next is ImportEntry ? "import" : "export")})");
            link = next;
        }
        var resource = material.ClassName is "Material" or "DecalMaterial"
            ? LegendaryExplorerCore.Unreal.BinaryConverters.ObjectBinary.From<LegendaryExplorerCore.Unreal.BinaryConverters.Material>(material).SM3MaterialResource
            : LegendaryExplorerCore.Unreal.BinaryConverters.ObjectBinary.From<LegendaryExplorerCore.Unreal.BinaryConverters.MaterialInstance>(material).SM3StaticPermutationResource;
        foreach (int t in resource?.UniformExpressionTextures ?? [])
            Console.WriteLine($"texture {t}: {package.GetEntry(t)?.InstancedFullPath} ({package.GetEntry(t)?.ClassName})");
        var (map, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(package, ShaderCacheManipulator.GetShaderMapId(material), null, true, []);
        Console.WriteLine($"map: {(map == null ? "none" : $"{map.Shaders.Count} global types")}");
        foreach (var mesh in map?.MeshShaderMaps ?? [])
            Console.WriteLine($"vf {mesh.VertexFactoryType.Name}: {string.Join(", ", mesh.Shaders.Keys.Select(k => k.Name).OrderBy(n => n))}");
    }
}

/// <summary>Opt-in probe (LEX_DECAL_PROBE=package|materialName): the decal components using a material, with their properties.</summary>
[TestClass]
public class DecalProbeTests
{
    [TestMethod]
    public void DumpDecals()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_DECAL_PROBE");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_DECAL_PROBE.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        using var package = MEPackageHandler.OpenMEPackage(parts[0], forceLoadFromDisk: true);
        foreach (var component in package.Exports.Where(e => e.IsA("DecalComponent") && !e.IsDefaultObject))
        {
            var material = component.GetProperty<ObjectProperty>("DecalMaterial")?.ResolveToEntry(package);
            if (material == null || !material.ObjectName.Instanced.Contains(parts[1], StringComparison.OrdinalIgnoreCase)) continue;
            var actor = component.Parent as ExportEntry;
            string actorProps = actor == null ? "" : string.Join(" ", actor.GetProperties().Where(p => p.Name.Instanced is not ("Tag" or "Components" or "DrawScale3D" or "CreationTime"))
                .Select(p => $"{p.Name.Instanced}={Describe(p)}"));
            string componentProps = string.Join(" ", component.GetProperties().Where(p => p.Name.Instanced is not ("HitTangent" or "HitBinormal" or "HitNormal" or "LightingChannels" or "DecalMaterial" or "ReplacementPrimitive"))
                .Select(p => $"{p.Name.Instanced}={Describe(p)}"));
            Console.WriteLine($"{actor?.ObjectName.Instanced} [{actorProps}] comp[{componentProps}] binary {component.GetBinaryData().Length}");
            if (parts.Length > 2 && !parts[2].Split(',').Contains(actor?.ObjectName.Instanced)) continue;
            foreach (var receiver in ObjectBinary.From<DecalComponent>(component).StaticReceivers)
            {
                var receiverComponent = package.GetEntry(receiver.Component);
                string lightMap = receiver.LightMap switch
                {
                    LightMap_2D l => $"2D tex {Name(l.Texture1)},{Name(l.Texture2)},{Name(l.Texture3)} scale {l.ScaleVector1} coord {l.CoordinateScale}/{l.CoordinateBias}",
                    LightMap_1D l => $"1D owner {Name(l.Owner)} samples {l.DirectionalSamples?.Length}",
                    LightMap_4or6 l => $"{l.LightMapType} tex {Name(l.Texture1)},{Name(l.Texture2)},{Name(l.Texture3)}",
                    null => "none",
                    var l => l.LightMapType.ToString()
                };
                var lmc = receiver.Vertices.Select(v => v.LightMapCoordinate).ToList();
                Console.WriteLine($"    receiver {receiver.Component} {receiverComponent?.InstancedFullPath} ({receiverComponent?.ClassName}) verts {receiver.Vertices.Length} tris {receiver.NumTriangles} " +
                                  $"lightmap {lightMap} shadowmaps [{string.Join(",", (receiver.ShadowMap1D ?? []).Select(Name))}] data {receiver.Data} instance {receiver.InstanceIndex} " +
                                  $"lmUV x {lmc.Min(c => c.X):G3}..{lmc.Max(c => c.X):G3} y {lmc.Min(c => c.Y):G3}..{lmc.Max(c => c.Y):G3}");
            }
            foreach (var receiver in ObjectBinary.From<DecalComponent>(component).StaticReceivers)
            {
                var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
                foreach (var v in receiver.Vertices) { min = System.Numerics.Vector3.Min(min, v.Position); max = System.Numerics.Vector3.Max(max, v.Position); }
                string lightSamples = receiver.LightMap is LightMap_1D l1 ? $"lm hash {Hash(l1.DirectionalSamples.SelectMany(d => new[] { d.Coefficient1.GetHashCode(), d.Coefficient2.GetHashCode(), d.Coefficient3.GetHashCode() }))} guids {string.Join(",", l1.LightGuids.Take(2))}" : "";
                string shadows = string.Join(" ", (receiver.ShadowMap1D ?? []).Select(u => package.GetEntry(u) is ExportEntry e
                    ? (ObjectBinary.From<ShadowMap1D>(e) is var sm ? $"{e.ObjectName.Instanced}[n {sm.Samples.Length} hash {Hash(sm.Samples)} sum {sm.Samples.Sum(x => (long)x)} light {sm.LightGuid}]" : "") : $"{u}?"));
                Console.WriteLine($"    data recv {package.GetEntry(receiver.Component)?.Parent?.ObjectName.Instanced} bounds {min}..{max} {lightSamples} shadows {shadows}");
            }
            static int Hash(IEnumerable<int> values) { unchecked { int h = 17; foreach (int v in values) h = h * 31 + v; return h; } }
            string Name(int uIndex) => uIndex == 0 ? "0" : $"{uIndex}:{package.GetEntry(uIndex)?.ObjectName.Instanced}({package.GetEntry(uIndex)?.ClassName})";
        }
    }

    private static string Describe(Property p) => p switch
    {
        StructProperty s => "{" + string.Join(", ", s.Properties.Select(x => $"{x.Name.Instanced}={Describe(x)}")) + "}",
        ArrayPropertyBase a => $"[{a.Count} items]",
        FloatProperty f => f.Value.ToString("G6"),
        IntProperty i => i.Value.ToString(),
        BoolProperty b => b.Value.ToString(),
        ObjectProperty o => o.Value.ToString(),
        EnumProperty e => e.Value.Instanced,
        NameProperty n => n.Value.Instanced,
        ByteProperty b => b.Value.ToString(),
        _ => p.GetType().Name
    };
}

/// <summary>Opt-in probe (LEX_SMC_PROBE=package|actorName,...): each static mesh component's LOD lightmaps and shadow maps.</summary>
[TestClass]
public class StaticMeshLightingProbeTests
{
    [TestMethod]
    public void DumpStaticMeshLighting()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_SMC_PROBE");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_SMC_PROBE.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        using var package = MEPackageHandler.OpenMEPackage(parts[0], forceLoadFromDisk: true);
        string Name(int u) => u == 0 ? "0" : $"{u}:{package.GetEntry(u)?.ObjectName.Instanced}({package.GetEntry(u)?.ClassName})";
        foreach (var component in package.Exports.Where(e => e.ClassName == "StaticMeshComponent" && e.Parent is ExportEntry a && parts[1].Split(',').Contains(a.ObjectName.Instanced)))
        {
            var props = string.Join(" ", component.GetProperties().Where(p => p.Name.Instanced is "bForceDirectLightMap" or "bOverrideLightMapResolution" or "OverriddenLightMapResolution" or "bAcceptsLights" or "bAcceptsDynamicLights" or "bUsePrecomputedShadows" or "CastShadow" or "bCastDynamicShadow" or "bAcceptsDecals" or "bAcceptsStaticDecals" or "bAcceptsDynamicDecals" or "LightEnvironment" or "bAcceptsDynamicDominantLightShadows")
                .Select(p => $"{p.Name.Instanced}={(p is BoolProperty b ? b.Value.ToString() : p is ObjectProperty o ? o.Value.ToString() : p is IntProperty i ? i.Value.ToString() : p.GetType().Name)}"));
            Console.WriteLine($"{component.InstancedFullPath} [{props}]");
            foreach (var lod in ObjectBinary.From<StaticMeshComponent>(component).LODData)
            {
                string lightMap = lod.LightMap switch
                {
                    LightMap_2D l => $"2D tex {Name(l.Texture1)},{Name(l.Texture2)},{Name(l.Texture3)} scale {l.ScaleVector1}",
                    LightMap_1D l => $"1D samples {l.DirectionalSamples?.Length} scale {l.ScaleVector1}/{l.ScaleVector2}/{l.ScaleVector3} guids {l.LightGuids?.Length}",
                    LightMap_4or6 l => $"{l.LightMapType} tex {Name(l.Texture1)},{Name(l.Texture2)},{Name(l.Texture3)}",
                    null => "none",
                    var l => l.LightMapType.ToString()
                };
                Console.WriteLine($"    lod lightmap {lightMap} shadowmaps2D [{string.Join(",", lod.ShadowMaps.Select(Name))}] shadow1D [{string.Join(",", lod.ShadowVertexBuffers.Select(Name))}]");
            }
        }
    }
}

/// <summary>
/// Opt-in tool (LEX_DECAL_CLEAR=package|actorName,...): clears the named decal actors' stored static receivers and saves the package,
/// so the game projects those decals at load instead of using the stored geometry. Back the package up first.
/// </summary>
[TestClass]
public class DecalReceiverClearTool
{
    [TestMethod]
    public void ClearStaticDecalReceivers()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_DECAL_CLEAR");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_DECAL_CLEAR.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        var actors = parts[1].Split(',');
        using var package = MEPackageHandler.OpenMEPackage(parts[0], forceLoadFromDisk: true);
        int cleared = 0;
        foreach (var component in package.Exports.Where(e => e.IsA("DecalComponent") && !e.IsDefaultObject && e.Parent is ExportEntry a && actors.Contains(a.ObjectName.Instanced)))
        {
            var binary = ObjectBinary.From<DecalComponent>(component);
            Console.WriteLine($"{component.InstancedFullPath}: clearing {binary.StaticReceivers.Length} stored receivers");
            binary.StaticReceivers = [];
            component.WriteBinary(binary);
            var receivers = component.GetProperty<ArrayProperty<ObjectProperty>>("DecalReceivers");
            if (receivers != null) Console.WriteLine($"    DecalReceivers property: {receivers.Count} entries (kept)");
            cleared++;
        }
        Assert.AreEqual(actors.Length, cleared, "Not every named decal actor was found.");
        package.Save();
    }
}

/// <summary>
/// Opt-in tool (LEX_EMPTY_LEVELS=game|path1;path2;...): writes a new empty level at each path, as Package Editor's New level file does,
/// replacing any file there. Back the files up first.
/// </summary>
[TestClass]
public class EmptyLevelTool
{
    [TestMethod]
    public void CreateEmptyLevels()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_EMPTY_LEVELS");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_EMPTY_LEVELS.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        var game = Enum.Parse<MEGame>(parts[0]);
        foreach (string path in parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            MEPackageHandler.CreateEmptyLevel(path, game);
            using var check = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            Assert.AreEqual(game, check.Game, path);
            Console.WriteLine($"{path}: {check.Game}, {check.ExportCount} exports, level {check.FindExport("TheWorld.PersistentLevel") != null}");
        }
    }

    /// <summary>
    /// Opt-in tool (LEX_EMPTY_PACKAGES=game|path1;path2;...): writes a new empty package at each path, as Package Editor's New empty package
    /// does, replacing any file there. Back the files up first.
    /// </summary>
    [TestMethod]
    public void CreateEmptyPackages()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_EMPTY_PACKAGES");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_EMPTY_PACKAGES.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        var game = Enum.Parse<MEGame>(parts[0]);
        foreach (string path in parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            MEPackageHandler.CreateAndSavePackage(path, game);
            using var check = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            Assert.AreEqual(game, check.Game, path);
            Console.WriteLine($"{path}: {check.Game}, {check.ExportCount} exports, {check.ImportCount} imports");
        }
    }

    /// <summary>Opt-in probe (LEX_LEVEL_NAMES=path1;path2;...): each package's game, top-level exports and name table.</summary>
    [TestMethod]
    public void DumpLevelNames()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_LEVEL_NAMES");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_LEVEL_NAMES.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        foreach (string path in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            Console.WriteLine($"{Path.GetFileName(path)}: {package.Game} exports [{string.Join(", ", package.Exports.Select(e => $"{e.InstancedFullPath}({e.ClassName})"))}] names [{string.Join(", ", package.Names)}]");
        }
    }
}

/// <summary>
/// Opt-in probe (LEX_MATERIAL_USERS=package|materialName[|compare.pcc]): the static mesh components that draw with a material, with their
/// lightmaps, and the same components in another package (the source level) for comparison.
/// </summary>
[TestClass]
public class MaterialUsersProbeTests
{
    [TestMethod]
    public void DumpMaterialUsers()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_MATERIAL_USERS");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_MATERIAL_USERS.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        using var package = MEPackageHandler.OpenMEPackage(parts[0], forceLoadFromDisk: true);
        using var compare = parts.Length > 2 ? MEPackageHandler.OpenMEPackage(parts[2], forceLoadFromDisk: true) : null;
        var cache = new PackageCache();
        int shown = 0;
        foreach (var smc in package.Exports.Where(e => e.ClassName == "StaticMeshComponent" && !e.IsDefaultObject))
        {
            var meshEntry = smc.GetProperty<ObjectProperty>("StaticMesh")?.ResolveToEntry(package);
            var mesh = meshEntry as ExportEntry ?? (meshEntry is ImportEntry imp && EntryImporter.TryResolveImport(imp, out var r, cache) ? r : null);
            IEnumerable<string> overrides = smc.GetProperty<ArrayProperty<ObjectProperty>>("Materials")?.Select(o => o.ResolveToEntry(package)?.ObjectName.Instanced) ?? [];
            IEnumerable<string> meshMaterials = mesh == null ? [] : ObjectBinary.From<StaticMesh>(mesh).LODModels.SelectMany(l => l.Elements).Select(el => mesh.FileRef.GetEntry(el.Material)?.ObjectName.Instanced);
            if (!overrides.Concat(meshMaterials).Contains(parts[1])) continue;
            if (shown++ >= 6) break;
            Console.WriteLine($"{smc.InstancedFullPath} mesh {meshEntry?.InstancedFullPath} ({(meshEntry is ImportEntry ? "import" : "export")})");
            Describe(package, smc, "    LE3");
            if (compare?.FindExport(smc.InstancedFullPath) is { } other) Describe(compare, other, "    src");
        }
        Console.WriteLine($"{shown} users");

        static void Describe(IMEPackage p, ExportEntry smc, string label)
        {
            string Name(int u) => u == 0 ? "0" : $"{p.GetEntry(u)?.ObjectName.Instanced}({p.GetEntry(u)?.ClassName})";
            var props = string.Join(" ", smc.GetProperties().Where(x => x.Name.Instanced is "bAcceptsLights" or "bUsePrecomputedShadows" or "LightingChannels" or "bForceDirectLightMap" or "LightEnvironment" or "CastShadow")
                .Select(x => $"{x.Name.Instanced}={(x is BoolProperty b ? b.Value.ToString() : x is ObjectProperty o ? o.Value.ToString() : x.GetType().Name)}"));
            foreach (var lod in ObjectBinary.From<StaticMeshComponent>(smc).LODData.Take(1))
            {
                string lm = lod.LightMap switch
                {
                    LightMap_2D l => $"2D {Name(l.Texture1)},{Name(l.Texture2)},{Name(l.Texture3)},{Name(l.Texture4)} scale {l.ScaleVector1}/{l.ScaleVector2}/{l.ScaleVector3}",
                    LightMap_1D l => $"1D samples {l.DirectionalSamples?.Length} scale {l.ScaleVector1}/{l.ScaleVector2}/{l.ScaleVector3}",
                    null => "none",
                    var l => l.LightMapType.ToString()
                };
                Console.WriteLine($"{label} [{props}] lightmap {lm} shadow2D [{string.Join(",", lod.ShadowMaps.Select(Name))}] shadow1D [{string.Join(",", lod.ShadowVertexBuffers.Select(Name))}]");
            }
        }
    }
}

/// <summary>
/// Opt-in probe (LEX_LIGHT_PROBE=package|materialName|levelFile;levelFile...): for the first component drawing with the material, its lighting
/// channels and the lights its shadow maps are for, found by LightGuid in the level files, with their channels and state.
/// </summary>
[TestClass]
public class LightProbeTests
{
    [TestMethod]
    public void DumpLightsOfMaterialUser()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_LIGHT_PROBE");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_LIGHT_PROBE.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        using var package = MEPackageHandler.OpenMEPackage(parts[0], forceLoadFromDisk: true);
        var cache = new PackageCache();
        ExportEntry user = null;
        foreach (var smc in package.Exports.Where(e => e.ClassName == "StaticMeshComponent" && !e.IsDefaultObject))
        {
            var meshEntry = smc.GetProperty<ObjectProperty>("StaticMesh")?.ResolveToEntry(package);
            var mesh = meshEntry as ExportEntry ?? (meshEntry is ImportEntry imp && EntryImporter.TryResolveImport(imp, out var r, cache) ? r : null);
            if (mesh != null && ObjectBinary.From<StaticMesh>(mesh).LODModels.SelectMany(l => l.Elements).Any(el => mesh.FileRef.GetEntry(el.Material)?.ObjectName.Instanced == parts[1]) &&
                ObjectBinary.From<StaticMeshComponent>(smc).LODData.FirstOrDefault()?.ShadowMaps.Length > 0) { user = smc; break; }
        }
        Assert.IsNotNull(user, "no user with shadow maps");
        Console.WriteLine($"user {user.InstancedFullPath} channels {Channels(user)} {Flags(user)}");
        var guids = ObjectBinary.From<StaticMeshComponent>(user).LODData[0].ShadowMaps
            .Select(u => package.GetEntry(u) is ExportEntry sm ? sm.GetProperty<StructProperty>("LightGuid") : null).Where(g => g != null)
            .Select(g => CommonStructs.GetGuid(g)).ToList();
        Console.WriteLine($"shadow map lights: {string.Join(", ", guids)}");
        foreach (string file in parts[2].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            using var level = MEPackageHandler.OpenMEPackage(file, forceLoadFromDisk: true);
            foreach (var light in level.Exports.Where(e => e.ClassName.EndsWith("LightComponent", StringComparison.Ordinal) && !e.IsDefaultObject))
            {
                var g = light.GetProperty<StructProperty>("LightGuid");
                if (g == null || !guids.Contains(CommonStructs.GetGuid(g))) continue;
                Console.WriteLine($"  {Path.GetFileName(file)}: {light.InstancedFullPath} ({light.ClassName}) channels {Channels(light)} {Flags(light)}");
            }
        }

        static string Channels(ExportEntry e) => e.GetProperty<StructProperty>("LightingChannels") is { } c
            ? string.Join(",", c.Properties.OfType<BoolProperty>().Where(b => b.Value).Select(b => b.Name.Instanced)) : "default";
        static string Flags(ExportEntry e) => string.Join(" ", e.GetProperties().Where(p => p is BoolProperty or FloatProperty && p.Name.Instanced is
                "bEnabled" or "Brightness" or "bCastDynamicShadows" or "bCastStaticShadows" or "bAffectsDefaultLightEnvironment" or "bForceDynamicLight" or "Radius" or "bAcceptsLights" or "bAcceptsDynamicLights" or "bUsePrecomputedShadows" or "bAllowPreShadow" or "LightAffectsClassification")
            .Select(p => $"{p.Name.Instanced}={(p is BoolProperty b ? b.Value.ToString() : ((FloatProperty)p).Value.ToString("G4"))}"));
    }
}

/// <summary>
/// Opt-in probe (LEX_MESH_COMPARE=ported.pcc|source.pcc|componentPath[;componentPath]): a component's static mesh in both packages (resolved
/// through imports), with vertex counts, lightmap coordinate index and resolution, and its materials.
/// </summary>
[TestClass]
public class MeshCompareProbeTests
{
    [TestMethod]
    public void CompareComponentMeshes()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_MESH_COMPARE");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_MESH_COMPARE.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        using var ported = MEPackageHandler.OpenMEPackage(parts[0], forceLoadFromDisk: true);
        using var source = MEPackageHandler.OpenMEPackage(parts[1], forceLoadFromDisk: true);
        var cache = new PackageCache();
        foreach (string path in parts[2].Split(';'))
            foreach (var p in new[] { ported, source })
            {
                var smc = p.FindExport(path);
                if (smc == null) { Console.WriteLine($"{p.Game}: {path} not found"); continue; }
                var meshEntry = smc.GetProperty<ObjectProperty>("StaticMesh")?.ResolveToEntry(p);
                var mesh = meshEntry as ExportEntry ?? (meshEntry is ImportEntry imp && EntryImporter.TryResolveImport(imp, out var r, cache) ? r : null);
                if (mesh == null) { Console.WriteLine($"{p.Game}: {path} mesh {meshEntry?.InstancedFullPath} unresolved"); continue; }
                var sm = ObjectBinary.From<StaticMesh>(mesh);
                var lod = sm.LODModels[0];
                Console.WriteLine($"{p.Game}: {path} mesh {mesh.InstancedFullPath} in {Path.GetFileName(mesh.FileRef.FilePath)} ({(meshEntry is ImportEntry ? "import" : "export")}) " +
                                  $"verts {lod.NumVertices} uvs {lod.VertexBuffer.NumTexCoords} lmIndex {mesh.GetProperty<IntProperty>("LightMapCoordinateIndex")?.Value} lmRes {mesh.GetProperty<IntProperty>("LightMapResolution")?.Value} " +
                                  $"materials [{string.Join(", ", lod.Elements.Select(el => mesh.FileRef.GetEntry(el.Material)?.ObjectName.Instanced))}]");
            }
    }
}

/// <summary>Opt-in probe (LEX_EXPORT_PROPS=package|path[;path]): an export's properties (structs flattened) and binary size, and its texture's format.</summary>
[TestClass]
public class ExportPropsProbeTests
{
    [TestMethod]
    public void DumpExportProps()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_EXPORT_PROPS");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_EXPORT_PROPS.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        var parts = spec.Split('|');
        using var package = MEPackageHandler.OpenMEPackage(parts[0], forceLoadFromDisk: true);
        var targets = parts[1].StartsWith("class:", StringComparison.Ordinal)
            ? package.Exports.Where(e => e.ClassName == parts[1][6..] && !e.IsDefaultObject).Take(4).Select(e => e.InstancedFullPath).ToArray()
            : parts[1].Split(';');
        foreach (string path in targets)
        {
            var export = package.FindExport(path);
            if (export == null) { Console.WriteLine($"{path}: not found"); continue; }
            Console.WriteLine($"{path} ({export.ClassName}) binary {export.GetBinaryData().Length}: {Describe(export.GetProperties())}");
            foreach (var o in export.GetProperties().OfType<ObjectProperty>())
                if (o.ResolveToEntry(package) is ExportEntry t && t.IsTexture())
                    Console.WriteLine($"    {o.Name.Instanced} -> {t.InstancedFullPath} ({t.ClassName}): {Describe(t.GetProperties())}");
        }
        static string Describe(PropertyCollection props) => string.Join(" ", props.Select(p => $"{p.Name.Instanced}={Value(p)}"));
        static string Value(Property p) => p switch
        {
            StructProperty s => "{" + string.Join(",", s.Properties.Select(x => $"{x.Name.Instanced}={Value(x)}")) + "}",
            BoolProperty b => b.Value.ToString(), FloatProperty f => f.Value.ToString("G5"), IntProperty i => i.Value.ToString(),
            ObjectProperty o => o.Value.ToString(), EnumProperty e => e.Value.Instanced, NameProperty n => n.Value.Instanced, ByteProperty b => b.Value.ToString(),
            ArrayPropertyBase a => $"[{a.Count}]", _ => p.GetType().Name
        };
    }
}

/// <summary>Opt-in probe (LEX_LIGHTING_STATS=file;file...): per level, static mesh component LOD0 lighting: lightmap type, with and without static shadow maps.</summary>
[TestClass]
public class LightingStatsProbeTests
{
    [TestMethod]
    public void DumpLightingStats()
    {
        string spec = Environment.GetEnvironmentVariable("LEX_LIGHTING_STATS");
        if (string.IsNullOrEmpty(spec)) Assert.Inconclusive("Set LEX_LIGHTING_STATS.");
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
        foreach (string file in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            using var package = MEPackageHandler.OpenMEPackage(file, forceLoadFromDisk: true);
            var counts = new Dictionary<string, int>();
            foreach (var smc in package.Exports.Where(e => e.ClassName == "StaticMeshComponent" && !e.IsDefaultObject && e.InstancedFullPath.StartsWith("TheWorld", StringComparison.Ordinal)))
            {
                var lod = ObjectBinary.From<StaticMeshComponent>(smc).LODData.FirstOrDefault();
                if (lod == null) continue;
                string key = $"{lod.LightMap?.LightMapType ?? ELightMapType.LMT_None}{(lod.ShadowMaps.Length > 0 ? "+shadow2D" : "")}{(lod.ShadowVertexBuffers.Length > 0 ? "+shadow1D" : "")}";
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            Console.WriteLine($"{Path.GetFileName(file)} ({package.Game}): {string.Join(", ", counts.OrderByDescending(c => c.Value).Select(c => $"{c.Key} {c.Value}"))}");
        }
    }
}

