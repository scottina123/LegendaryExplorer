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
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Metadata = LegendaryExplorerCore.Shaders.LE3MaterialShaderCompiler.TextureCompilationMetadata;
using Dimension = LegendaryExplorerCore.Shaders.LE3MaterialShaderCompiler.TextureDimension;

namespace LegendaryExplorer.Tests.Tools.MaterialShaderCompilation;

/// <summary>
/// Machine-local, opt-in checks of <see cref="LE3MaterialRecompiler"/> against the installed games: set LEX_MATERIAL_RECOMPILE_IT=1
/// (and have LE1/LE2/LE3 installed where LEX finds them). Nothing in the game folder is written.
/// <para>
/// Cooked materials in all three games usually keep only their parameter nodes, so most can't be recompiled faithfully: these tests
/// check they're refused with nothing installed (never compiled to black), and that a material with a real graph compiles, installs
/// and renders. Optional: LEX_MATERIAL_RECOMPILE_IT_COUNT (materials per cross-game port, default 30),
/// LEX_MATERIAL_RECOMPILE_IT_SWEEP (an LE3 package for the guard sweep), LEX_MATERIAL_RECOMPILE_IT_RENDER_OUT (where to save the
/// authored-material package, to render it with LECCLI render-mesh).
/// </para>
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class LE3MaterialRecompilerIntegrationTests
{
    private static readonly object InitLock = new();
    private static bool initialized;

    [TestInitialize]
    public void Setup()
    {
        if (Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOMPILE_IT") != "1")
            Assert.Inconclusive("Set LEX_MATERIAL_RECOMPILE_IT=1 to run the material recompile integration tests.");
        lock (InitLock)
        {
            if (initialized) return;
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            LegendaryExplorerCoreLib.InitLib(TaskScheduler.FromCurrentSynchronizationContext(), _ => { });
            initialized = true;
        }
        if (!LE3MaterialRecompiler.CanRecompile(MEGame.LE3, out string reason)) Assert.Inconclusive(reason);
    }

    [TestMethod]
    public void CrossGamePort_LE2_StrippedMaterialsAreRefused() =>
        PortAndRecompile(Path.Combine(LE2Directory.CookedPCPath, "BioA_CitHub_500Udina.pcc"));

    [TestMethod]
    public void CrossGamePort_LE1_StrippedMaterialsAreRefused() =>
        PortAndRecompile(Path.Combine(LE1Directory.CookedPCPath, "BIOA_NOR10_03_DSG.pcc"));

    /// <summary>Ports cooked materials into LE3 as the Package Editor does and recompiles them: each is either installed and checks out, or refused with nothing installed.</summary>
    private static void PortAndRecompile(string sourcePath)
    {
        if (!File.Exists(sourcePath)) Assert.Inconclusive($"{sourcePath} wasn't found.");
        int count = int.TryParse(Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOMPILE_IT_COUNT"), out int n) ? n : 30;
        using var source = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
        var materials = source.Exports.Where(e => e.ClassName == "Material" && !e.IsDefaultObject &&
                                                  e.GetProperty<EnumProperty>("LightingModel")?.Value.Name != "MLM_Unlit").Take(count).ToList();
        if (materials.Count == 0) Assert.Inconclusive($"{sourcePath} has no lit materials.");

        using var dest = MEPackageHandler.CreateMemoryEmptyPackage("LEXRecompileIT_Port.pcc", MEGame.LE3);
        var ported = new List<ExportEntry>();
        var portedFrom = new Dictionary<ExportEntry, ExportEntry>();
        foreach (var material in materials)
        {
            // The Package Editor porting dialog's defaults: memory-safe import porting on
            var rop = new RelinkerOptionsPackage { IsCrossGame = true, Cache = new PackageCache(), ImportExportDependencies = true, PortImportsMemorySafe = true };
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, material, dest, null, true, rop, out IEntry newEntry);
            ported.Add((ExportEntry)newEntry);
            portedFrom[(ExportEntry)newEntry] = material;
        }
        var flagsBefore = ported.ToDictionary(m => m, m => m.GetProperty<BoolProperty>("bUsedWithStaticLighting")?.Value);

        var outcomes = new LE3MaterialRecompiler().Recompile(ported, m => m.GetProperty<BoolProperty>("bUsedWithStaticLighting") == null
            ? LE3MaterialUsageAdvisor.LightmappedStaticMesh(simpleLightMaps: false) : [], sourceOf: m => portedFrom.GetValueOrDefault(m));
        foreach (var outcome in outcomes) Console.WriteLine(outcome);
        foreach (var outcome in outcomes)
        {
            if (outcome.Success) { CheckInstalled(outcome.Material); continue; }
            Assert.IsTrue(ShaderCacheManipulator.IsMaterialBroken(outcome.Material), $"{outcome.Material.InstancedFullPath}: shaders were installed although it was refused.");
            Assert.AreEqual(flagsBefore[outcome.Material], outcome.Material.GetProperty<BoolProperty>("bUsedWithStaticLighting")?.Value,
                $"{outcome.Material.InstancedFullPath}: flags weren't restored after it was refused.");
        }
        Console.WriteLine($"{Path.GetFileName(sourcePath)}: {outcomes.Count(o => o.Success)} recompiled, {outcomes.Count(o => !o.Success)} refused");
    }

    /// <summary>Lightmass path on a cooked LE3 material without bUsedWithStaticLighting: its stripped graph is recovered from its shipping shaders and recompiled with the flag and lightmap shaders.</summary>
    [TestMethod]
    public void StaticLighting_CookedMaterial_IsRecoveredWithLightmapShaders()
    {
        string sourcePath = Path.Combine(LE3Directory.CookedPCPath, "BioA_CitHub_Embassy.pcc");
        if (!File.Exists(sourcePath)) Assert.Inconclusive($"{sourcePath} wasn't found.");
        using var source = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
        var material = source.Exports.First(e => e.ClassName == "Material" && e.GetProperty<EnumProperty>("LightingModel")?.Value.Name != "MLM_Unlit");
        using var dest = MEPackageHandler.CreateMemoryEmptyPackage("LEXRecompileIT_Lightmass.pcc", MEGame.LE3);
        EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, material, dest, null, true,
            new RelinkerOptionsPackage { Cache = new PackageCache(), ImportExportDependencies = true }, out IEntry newEntry);
        var copy = (ExportEntry)newEntry;
        copy.WriteProperty(new BoolProperty(false, "bUsedWithStaticLighting"));

        var recompiler = new LE3MaterialRecompiler();
        Assert.IsTrue(recompiler.TryEnableStaticLighting(copy, p => ReferenceEquals(p, dest), out string message), message);
        Console.WriteLine(message);
        StringAssert.Contains(message, "Recovered");
        Assert.AreEqual(true, copy.GetProperty<BoolProperty>("bUsedWithStaticLighting")?.Value);
        CheckInstalled(copy);
    }

    /// <summary>
    /// A material with a real graph (a red Constant3Vector into DiffuseColor, as authored in the material editor) compiles, installs with lightmap
    /// shaders, and survives a save and reload. The UDK Lightmass scene then gives a mesh drawing it a new lightmap instead of leaving it out.
    /// </summary>
    [TestMethod]
    public void AuthoredMaterial_CompilesInstallsAndGetsALightmap()
    {
        string levelPath = Path.Combine(LE3Directory.CookedPCPath, "BioA_Nor_100Cabin.pcc");
        if (!File.Exists(levelPath)) Assert.Inconclusive($"{levelPath} wasn't found.");
        string copyPath = Path.Combine(Path.GetTempPath(), $"LEXRecompileIT_{Guid.NewGuid():N}.pcc");
        File.Copy(levelPath, copyPath);
        try
        {
            using var level = MEPackageHandler.OpenMEPackage(copyPath, forceLoadFromDisk: true);
            var material = CreateRedMaterial(level);
            Assert.IsNull(LE3MaterialRecompiler.StrippedGraphProblem(material));

            // A static mesh with no lightmap, drawing it
            var component = level.Exports.First(e => e.ClassName == "StaticMeshComponent" && e.Parent is ExportEntry { ClassName: "StaticMeshCollectionActor" or "StaticMeshActor" } &&
                                                     ObjectBinary.From<StaticMeshComponent>(e).LODData is { Length: > 0 } lods && lods[0].LightMap?.LightMapType == ELightMapType.LMT_2D);
            var smc = ObjectBinary.From<StaticMeshComponent>(component);
            smc.LODData[0].LightMap = new LightMap { LightMapType = ELightMapType.LMT_None };
            component.WriteBinary(smc);
            var meshEntry = level.GetEntry(component.GetProperty<ObjectProperty>("StaticMesh").Value);
            var mesh = meshEntry as ExportEntry ?? (EntryImporter.TryResolveImport((ImportEntry)meshEntry, out var resolved, new PackageCache()) ? resolved : null);
            int slots = ObjectBinary.From<StaticMesh>(mesh).LODModels[0].Elements.Length;
            component.WriteProperty(new ArrayProperty<ObjectProperty>(Enumerable.Range(0, slots).Select(_ => new ObjectProperty(material.UIndex)), "Materials"));

            var log = new List<string>();
            using var builder = new LegendaryExplorer.Lightmaps.Lightmass.LightmassSceneBuilder(
                new LegendaryExplorer.Lightmaps.Lightmass.LightmassSceneOptions { MaterialRecompiler = new LE3MaterialRecompiler() });
            var scene = builder.Build([level], log.Add);
            foreach (string line in log.Where(l => l.Contains("recompiled") || l.Contains("stripped"))) Console.WriteLine(line);
            Assert.IsTrue(scene.Report.ContainsKey("material: recompiled for static lighting"), "The authored material wasn't recompiled.");
            CheckInstalled(material);
            var instance = scene.Instances.FirstOrDefault(i => i.Component == component && i.LodIndex == 0);
            Assert.IsTrue(instance?.AllocateLightmap == true, "The receiver got no new lightmap.");

            level.Save();
            using var reloaded = MEPackageHandler.OpenMEPackage(copyPath, forceLoadFromDisk: true);
            CheckInstalled(reloaded.FindExport(material.InstancedFullPath));

            if (Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOMPILE_IT_RENDER_OUT") is { Length: > 0 } renderOut)
            {
                File.Copy(copyPath, renderOut, overwrite: true);
                Console.WriteLine($"render with: LECCLI render-mesh --uindex {component.UIndex} (component) in {renderOut}");
            }
        }
        finally
        {
            File.Delete(copyPath);
        }
    }

    /// <summary>
    /// The bakers' option off (no recompiler): a mesh drawing a material without lightmap shaders is left out of the UDK Lightmass scene, and the
    /// material isn't recompiled.
    /// </summary>
    [TestMethod]
    public void RecompileOptionOff_LeavesTheMeshOutAndTheMaterialAlone()
    {
        string levelPath = Path.Combine(LE3Directory.CookedPCPath, "BioA_Nor_100Cabin.pcc");
        if (!File.Exists(levelPath)) Assert.Inconclusive($"{levelPath} wasn't found.");
        using var level = MEPackageHandler.OpenMEPackage(levelPath, forceLoadFromDisk: true);
        var material = CreateRedMaterial(level);
        var component = level.Exports.First(e => e.ClassName == "StaticMeshComponent" && e.Parent is ExportEntry { ClassName: "StaticMeshCollectionActor" or "StaticMeshActor" } &&
                                                 ObjectBinary.From<StaticMeshComponent>(e).LODData is { Length: > 0 } lods && lods[0].LightMap?.LightMapType == ELightMapType.LMT_2D);
        var smc = ObjectBinary.From<StaticMeshComponent>(component);
        smc.LODData[0].LightMap = new LightMap { LightMapType = ELightMapType.LMT_None };
        component.WriteBinary(smc);
        var meshEntry = level.GetEntry(component.GetProperty<ObjectProperty>("StaticMesh").Value);
        var mesh = meshEntry as ExportEntry ?? (EntryImporter.TryResolveImport((ImportEntry)meshEntry, out var resolved, new PackageCache()) ? resolved : null);
        int slots = ObjectBinary.From<StaticMesh>(mesh).LODModels[0].Elements.Length;
        component.WriteProperty(new ArrayProperty<ObjectProperty>(Enumerable.Range(0, slots).Select(_ => new ObjectProperty(material.UIndex)), "Materials"));

        var settings = new LegendaryExplorer.Lightmaps.Lightmass.LightmassBakeSettings { RecompileMaterialsWithoutLightmaps = false };
        LegendaryExplorer.Lightmaps.Lightmass.LightmassBake.ConfigureMaterialRecompiler(settings, level.Game, null);
        using var builder = new LegendaryExplorer.Lightmaps.Lightmass.LightmassSceneBuilder(settings.Scene);
        var scene = builder.Build([level], _ => { });
        Assert.IsFalse(scene.Report.ContainsKey("material: recompiled for static lighting"), "A material was recompiled with the option off.");
        Assert.IsTrue(ShaderCacheManipulator.IsMaterialBroken(material), "The material got shaders with the option off.");
        var instance = scene.Instances.FirstOrDefault(i => i.Component == component && i.LodIndex == 0);
        Assert.IsTrue(instance?.AllocateLightmap != true, "The mesh got a new lightmap although its material has no lightmap shaders.");
    }

    /// <summary>A lit Material whose DiffuseColor is a red Constant3Vector, with no shaders yet.</summary>
    private static ExportEntry CreateRedMaterial(IMEPackage package)
    {
        var material = new ExportEntry(package, 0, "LEXRecompileIT_Red", properties: new PropertyCollection(), binary: Material.Create())
        {
            Class = package.GetEntryOrAddImport("Engine.Material", "Class"),
            ObjectFlags = UnrealFlags.EObjectFlags.Public | UnrealFlags.EObjectFlags.LoadForClient | UnrealFlags.EObjectFlags.LoadForServer | UnrealFlags.EObjectFlags.LoadForEdit
        };
        package.AddExport(material);
        var constant = new ExportEntry(package, material.UIndex, "MaterialExpressionConstant3Vector",
            properties: new PropertyCollection { new FloatProperty(1, "R"), new FloatProperty(0, "G"), new FloatProperty(0, "B"), new ObjectProperty(material.UIndex, "Material") })
        {
            Class = package.GetEntryOrAddImport("Engine.MaterialExpressionConstant3Vector", "Class"),
        };
        package.AddExport(constant);
        var resource = MaterialResource.Create();
        resource.ID = Guid.NewGuid();
        var binary = Material.Create();
        binary.SM3MaterialResource = resource;
        material.WritePropertiesAndBinary(new PropertyCollection
        {
            new ArrayProperty<ObjectProperty>([new ObjectProperty(constant.UIndex)], "Expressions"),
            new StructProperty("ColorMaterialInput", [new ObjectProperty(constant.UIndex, "Expression")], "DiffuseColor"),
        }, binary);
        return material;
    }

    /// <summary>
    /// The guards against shipping: every base Material of a vanilla LE3 level is compiled under its shipping ID; any the guards accept must
    /// compile to the shipping base pass shaders. (At the time of writing they accept none of Embassy's 65: all are stripped.)
    /// </summary>
    [TestMethod]
    public void StrippedGraphGuards_AcceptOnlyMaterialsThatMatchShipping()
    {
        string path = Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOMPILE_IT_SWEEP") ?? Path.Combine(LE3Directory.CookedPCPath, "BioA_CitHub_Embassy.pcc");
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        var cache = new PackageCache();
        var sources = LE3ShaderSourceSnapshot.FromDirectory(LE3MaterialRecompiler.DefaultShaderDirectory, 1252);
        var service = new MaterialShaderCompilationService(new MaterialShaderCompilationOptions { Enabled = true, MaxDegreeOfParallelism = 16 });
        int strippedVisible = 0, strippedTextures = 0, differsFromShipping = 0, failed = 0, accepted = 0, matching = 0;
        var mismatches = new List<string>();
        foreach (var material in package.Exports.Where(e => e.ClassName == "Material" && !e.IsDefaultObject))
        {
            if (LE3MaterialRecompiler.StrippedGraphProblem(material) != null) { strippedVisible++; continue; }
            var resource = ObjectBinary.From<Material>(material).SM3MaterialResource;
            var shipping = RefShaderCacheReader.GetMaterialShaderMap(MEGame.LE3, ShaderCacheManipulator.GetShaderMapId(material), out _);
            if (shipping == null) continue;
            var result = service.CompileMaterialAsync(new MaterialShaderCompileRequest
            {
                Material = material, Resource = resource, ResourceId = resource.ID, FriendlyName = material.ObjectName.Instanced,
                SourceProvider = () => sources,
                GetTextureMetadata = e =>
                {
                    var t = e as ExportEntry ?? (EntryImporter.TryResolveImport((ImportEntry)e, out var r, cache) ? r : null);
                    if (t == null) return null;
                    bool cube = t.IsA("TextureCube");
                    var dim = cube ? Dimension.TextureCube : Dimension.Texture2D;
                    return !cube && !t.IsA("Texture2D") ? new Metadata(dim) : Metadata.FromProperties(dim, t.GetProperties());
                }
            }).GetAwaiter().GetResult();
            if (result.Outcome != MaterialShaderCompileOutcome.SucceededFragment) { failed++; continue; }
            if (LE3MaterialRecompiler.MissingCookedTexturesProblem(result, material) != null) { strippedTextures++; continue; }
            if (LE3MaterialRecompiler.ExistingShadersProblem(result, material) != null) { differsFromShipping++; continue; }
            accepted++;
            var ours = result.Fragment.Cache.MaterialShaderMaps.Values.Single();
            var shipMesh = shipping.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == "FLocalVertexFactory");
            var ourMesh = ours.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == "FLocalVertexFactory");
            bool same = shipMesh != null && ourMesh != null;
            foreach (var (type, shipRef) in shipMesh?.Shaders.Where(s => s.Key.Name.StartsWith("TBasePassPixelShader")) ?? [])
            {
                var ourRef = ourMesh?.Shaders.FirstOrDefault(s => s.Key == type).Value;
                if (ourRef == null) { same = false; break; }
                var a = RefShaderCacheReader.GetShaders(MEGame.LE3, [shipRef.Id], out _, out _)[0];
                if (a.InstructionCount != result.Fragment.Cache.Shaders[ourRef.Id].InstructionCount) { same = false; break; }
            }
            if (same) matching++; else mismatches.Add(material.InstancedFullPath);
        }
        Console.WriteLine($"{Path.GetFileName(path)}: visibly stripped {strippedVisible}, missing cooked textures {strippedTextures}, differs from shipping {differsFromShipping}, " +
                          $"compile failed {failed}, accepted {accepted} of which match shipping {matching}");
        foreach (string m in mismatches) Console.WriteLine("  MISMATCH " + m);
        Assert.AreEqual(accepted, matching, "The guards accepted materials that don't compile to the shipping shaders.");
    }

    /// <summary>
    /// For a visual check: ports static meshes (with their materials) from an LE1/LE2 package (LEX_MATERIAL_RECOMPILE_IT_RENDER_SOURCE,
    /// default LE2 Udina) into a new LE3 package as Clone All does, recompiles the ported materials from the source game's shaders, and saves it
    /// to LEX_MATERIAL_RECOMPILE_IT_RENDER_OUT to render with LECCLI render-mesh.
    /// </summary>
    [TestMethod]
    public void RenderCheck_PortMeshesAndRecompile()
    {
        string outPath = Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOMPILE_IT_RENDER_OUT");
        if (string.IsNullOrEmpty(outPath)) Assert.Inconclusive("Set LEX_MATERIAL_RECOMPILE_IT_RENDER_OUT to a .pcc path.");
        string sourcePath = Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOMPILE_IT_RENDER_SOURCE") ?? Path.Combine(LE2Directory.CookedPCPath, "BioA_CitHub_500Udina.pcc");
        string meshFilter = Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOMPILE_IT_RENDER_MESHES");
        using var source = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
        var meshes = source.Exports.Where(e => e.ClassName == "StaticMesh" && (meshFilter == null || meshFilter.Split(',').Contains(e.ObjectName.Instanced))).Take(8).ToList();
        if (File.Exists(outPath)) File.Delete(outPath);
        using var dest = MEPackageHandler.CreateAndOpenPackage(outPath, MEGame.LE3);
        var portedFrom = new Dictionary<ExportEntry, ExportEntry>();
        foreach (var mesh in meshes)
        {
            var rop = new RelinkerOptionsPackage { IsCrossGame = true, Cache = new PackageCache(), ImportExportDependencies = true, PortImportsMemorySafe = true };
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, mesh, dest, null, true, rop, out _);
            foreach (var (from, to) in rop.CrossPackageMap)
                if (from is ExportEntry f && to is ExportEntry t) portedFrom.TryAdd(t, f);
        }
        var outcomes = new LE3MaterialRecompiler().Recompile(dest.Exports.ToList(), m => m.GetProperty<BoolProperty>("bUsedWithStaticLighting") == null
            ? LE3MaterialUsageAdvisor.LightmappedStaticMesh(simpleLightMaps: false) : [], sourceOf: m => portedFrom.GetValueOrDefault(m));
        foreach (var outcome in outcomes) Console.WriteLine(outcome);
        dest.Save();
        foreach (var mesh in dest.Exports.Where(e => e.ClassName == "StaticMesh")) Console.WriteLine($"mesh {mesh.UIndex} {mesh.InstancedFullPath}");
        Assert.IsTrue(outcomes.Count(o => o.Success) > 0, "nothing recompiled");
    }

    private static void CheckInstalled(ExportEntry material)
    {
        Assert.IsNotNull(material);
        Assert.IsFalse(ShaderCacheManipulator.IsMaterialBroken(material), $"{material.InstancedFullPath} has no shader map after recompiling.");
        var key = ShaderCacheManipulator.GetShaderMapId(material);
        var cache = ObjectBinary.From<ShaderCache>(material.FileRef.FindExport("SeekFreeShaderCache", "ShaderCache"));
        Assert.IsTrue(cache.MaterialShaderMaps.TryGetValue(key, out var map), $"{material.InstancedFullPath}'s shader map isn't in its package.");
        foreach (var reference in map.Shaders.Select(s => s.Value).Concat(map.MeshShaderMaps.SelectMany(m => m.Shaders.Select(s => s.Value))))
            Assert.IsTrue(cache.Shaders.ContainsKey(reference.Id), $"{material.InstancedFullPath}: shader {reference.Id} is missing from the cache.");
        if (material.ClassName is "Material" or "DecalMaterial")
            Assert.AreEqual(map.ID, ObjectBinary.From<Material>(material).SM3MaterialResource.ID, "The resource ID must be the map's.");
        if (material.GetProperty<BoolProperty>("bUsedWithStaticLighting")?.Value == true)
            Assert.IsTrue(LE3MaterialRecompiler.HasLightmapShaders(material), $"{material.InstancedFullPath} is used with static lighting but has no lightmap shaders.");
        // Every texture the resource lists must be a texture in the material's own package
        var resource = material.ClassName is "Material" or "DecalMaterial"
            ? ObjectBinary.From<Material>(material).SM3MaterialResource
            : ObjectBinary.From<MaterialInstance>(material).SM3StaticPermutationResource;
        foreach (int texture in resource?.UniformExpressionTextures ?? [])
        {
            var entry = material.FileRef.GetEntry(texture);
            Assert.IsTrue(entry != null && (entry.ClassName.StartsWith("Texture", StringComparison.Ordinal) || entry.ClassName is "LightMapTexture2D" or "ShadowMapTexture2D" or "TextureMovie"),
                $"{material.InstancedFullPath}: UniformExpressionTextures lists {texture} ({entry?.InstancedFullPath ?? "nothing"}, {entry?.ClassName}), which isn't a texture.");
        }
    }

    /// <summary>
    /// LE2 → LE3 instances with their own static permutation, whose parent Material is in another package: the recovered
    /// resource's textures must stay the instance's own (not be read as the parent package's).
    /// </summary>
    [TestMethod]
    public void CrossGamePort_LE2_StaticPermutationInstances()
    {
        string sourcePath = Path.Combine(LE2Directory.CookedPCPath, "BioA_CitHub_500Udina.pcc");
        if (!File.Exists(sourcePath)) Assert.Inconclusive($"{sourcePath} wasn't found.");
        using var source = MEPackageHandler.OpenMEPackage(sourcePath, forceLoadFromDisk: true);
        var instances = source.Exports.Where(e => e.IsA("MaterialInstance") && !e.IsDefaultObject &&
                                                  e.GetProperty<BoolProperty>("bHasStaticPermutationResource")?.Value == true).ToList();
        if (instances.Count == 0) Assert.Inconclusive($"{sourcePath} has no static permutation instances.");
        using var dest = MEPackageHandler.CreateMemoryEmptyPackage("LEXRecompileIT_Instances.pcc", MEGame.LE3);
        var portedFrom = new Dictionary<ExportEntry, ExportEntry>();
        foreach (var instance in instances)
        {
            var rop = new RelinkerOptionsPackage { IsCrossGame = true, Cache = new PackageCache(), ImportExportDependencies = true, PortImportsMemorySafe = true };
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, instance, dest, null, true, rop, out IEntry newEntry);
            portedFrom[(ExportEntry)newEntry] = instance;
        }
        // Porting into an existing LE3 level keeps a parent the level already imports from LE3's own packages. LEX_MATERIAL_RECOMPILE_IT_PARENTS
        // names such an LE3 package (only read): ported instances whose parent it imports by the same name get that import as their parent.
        if (Environment.GetEnvironmentVariable("LEX_MATERIAL_RECOMPILE_IT_PARENTS") is { Length: > 0 } parentsPath)
        {
            using var parents = MEPackageHandler.OpenMEPackage(parentsPath, forceLoadFromDisk: true);
            var cache = new PackageCache();
            foreach (var ported in portedFrom.Keys)
            {
                if (ported.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(dest) is not ExportEntry parent) continue;
                if (parents.Imports.FirstOrDefault(i => i.ObjectName == parent.ObjectName && i.IsA("Material")) is not { } import ||
                    !EntryImporter.TryResolveImport(import, out _, cache)) continue;
                var parentImport = EntryImporter.GetOrAddCrossImportOrPackage(import.InstancedFullPath, parents, dest, new RelinkerOptionsPackage());
                ported.WriteProperty(new ObjectProperty(parentImport.UIndex, "Parent"));
            }
        }
        foreach (var ported in portedFrom.Keys)
            if (ported.GetProperty<ObjectProperty>("Parent")?.ResolveToEntry(dest) is { } parent)
                Console.WriteLine($"{ported.InstancedFullPath}: parent {parent.InstancedFullPath} ({(parent is ImportEntry ? "import" : "export")})");
        var outcomes = new LE3MaterialRecompiler().Recompile(portedFrom.Keys.ToList(), _ => [], sourceOf: m => portedFrom.GetValueOrDefault(m));
        foreach (var outcome in outcomes) Console.WriteLine(outcome);
        Assert.IsTrue(outcomes.Any(o => o.Success), "No instance recompiled.");
        foreach (var outcome in outcomes.Where(o => o.Success)) CheckInstalled(outcome.Material);
    }
}
