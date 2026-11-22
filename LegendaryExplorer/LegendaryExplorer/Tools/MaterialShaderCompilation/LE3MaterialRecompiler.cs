using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Compiler = LegendaryExplorerCore.Shaders.LE3MaterialShaderCompiler;

namespace LegendaryExplorer.Tools.MaterialShaderCompilation
{
    /// <summary>What happened to one material.</summary>
    /// <param name="Material">The Material, DecalMaterial or static-permutation MaterialInstance that was (or wasn't) recompiled.</param>
    /// <param name="FlagsSet">bUsedWith* flags set on the base Material so the game draws it where the package uses it.</param>
    /// <param name="ShaderCount">Shaders in the installed shader map.</param>
    public sealed record MaterialRecompileOutcome(ExportEntry Material, bool Success, string Message, IReadOnlyList<string> FlagsSet, int ShaderCount)
    {
        public override string ToString() => Success
            ? $"{Material.InstancedFullPath}: recompiled, {ShaderCount} shaders{(FlagsSet.Count > 0 ? $" (set {string.Join(", ", FlagsSet)})" : "")}.{Message}"
            : $"{Material.InstancedFullPath}: {Message}";
    }

    /// <summary>
    /// Recompiles LE3 materials with the offline LE3 material shader compiler and installs the result in the package that owns
    /// them, so the game draws them instead of the default (checkerboard) material. Used after porting materials into LE3 from
    /// LE1/LE2, whose shader maps the game can't use, and by the lightmap bakers for materials that aren't compiled for static
    /// lighting. Before compiling, the base Material's bUsedWith* flags are set for every use the caller says it needs, since
    /// those flags decide which shaders the game requires (and the game never sets them itself).
    /// </summary>
    /// <remarks>
    /// Each recompiled material gets a fresh resource ID (and shader map key), so its new shaders can't collide with a map the
    /// game already has. A static-permutation MaterialInstance of a recompiled base Material in the same package is recompiled
    /// too, as its shader set depends on the base's flags. Materials in other packages (imports) are never changed.
    /// Thread-safe: concurrent requests are serialized.
    /// </remarks>
    public sealed class LE3MaterialRecompiler
    {
        private static readonly ConcurrentDictionary<string, Lazy<LE3ShaderSourceSnapshot>> Sources = new(StringComparer.OrdinalIgnoreCase);
        private readonly PackageCache cache;
        private readonly string shaderDirectory;
        private readonly Lock gate = new();
        private readonly LE3MaterialShaderInstaller installer = new();
        private readonly Dictionary<ExportEntry, bool> staticLightingResults = new();
        private readonly HashSet<ExportEntry> failedOwners = [];

        /// <param name="cache">Resolves imported textures, fonts and parents. A new cache is used if null.</param>
        /// <param name="shaderDirectory">The LE3 Engine\Shaders folder; defaults to the configured LE3 install's.</param>
        public LE3MaterialRecompiler(PackageCache cache = null, string shaderDirectory = null)
        {
            this.cache = cache ?? new PackageCache();
            this.shaderDirectory = shaderDirectory ?? DefaultShaderDirectory;
        }

        public static string DefaultShaderDirectory => LE3Directory.DefaultGamePath is { Length: > 0 } root ? Path.Combine(root, "Engine", "Shaders") : null;

        /// <summary>
        /// After a cross-game port: each ported export and the source-game export it was ported from (for <see cref="Recompile"/>'s sourceOf).
        /// A source the ported package only imported (memory-safe import porting brings in the export it resolves to, e.g. an object of an
        /// LE2 BioA/BioD level that lives in its BioP) is resolved to that export, the way the port did.
        /// </summary>
        public static Dictionary<ExportEntry, ExportEntry> PortedSources(RelinkerOptionsPackage rop)
        {
            var sources = new Dictionary<ExportEntry, ExportEntry>();
            foreach (var (source, ported) in rop.CrossPackageMap)
                if (source is ExportEntry sourceExport && ported is ExportEntry portedExport)
                    sources.TryAdd(portedExport, sourceExport);
            foreach (var (source, ported) in rop.CrossPackageMap)
            {
                if (source is not ImportEntry import || ported is not ExportEntry portedExport || sources.ContainsKey(portedExport))
                    continue;
                if (EntryImporter.ResolveImport(import, rop.Cache, fileResolver: rop.SourceCustomImportFileResolver) is { } resolved)
                    sources[portedExport] = resolved;
            }
            return sources;
        }

        /// <summary>
        /// Whether a material a cross-game port brought in needs recompiling: not when it was ported from the destination game itself (a donor
        /// that replaced the source game's object) and already has its shaders.
        /// </summary>
        public static bool NeedsRecompileAfterPort(ExportEntry ported, ExportEntry portedFrom) =>
            portedFrom == null || portedFrom.Game != ported.Game || ShaderCacheManipulator.IsMaterialBroken(ported);

        /// <summary>Whether materials of <paramref name="game"/> can be recompiled here; if not, why.</summary>
        public static bool CanRecompile(MEGame game, out string reason, string shaderDirectory = null)
        {
            shaderDirectory ??= DefaultShaderDirectory;
            reason = game != MEGame.LE3
                ? $"Recompiling material shaders is only available for LE3; {game} materials keep the shaders they have."
                : shaderDirectory == null || !File.Exists(Path.Combine(shaderDirectory, "MaterialTemplate.usf"))
                    ? $"LE3's shader sources (Engine\\Shaders) weren't found{(shaderDirectory == null ? "" : $" at {shaderDirectory}")}. Set the LE3 game path in LEX's settings."
                    : null;
            return reason == null;
        }

        /// <summary>Materials of <paramref name="package"/> the package draws, grouped by base Material, with every use it puts them to.</summary>
        public Dictionary<ExportEntry, HashSet<LE3MaterialUsage>> ScanUses(IMEPackage package)
        {
            var uses = new Dictionary<ExportEntry, HashSet<LE3MaterialUsage>>();
            foreach (var use in LE3MaterialUsageScanner.Collect(package, ResolveImport))
            {
                if (use.BaseMaterial == null) continue;
                if (!uses.TryGetValue(use.BaseMaterial, out var set)) uses[use.BaseMaterial] = set = [];
                set.Add(use.Usage);
            }
            return uses;
        }

        /// <summary>
        /// Recompiles <paramref name="materials"/> (base Materials/DecalMaterials, or MaterialInstances with a static permutation; other
        /// entries are ignored) in their own packages. Base Materials first get the flags <paramref name="requiredUses"/> asks for.
        /// </summary>
        /// <param name="requiredUses">The uses each base Material must support; null for none beyond its current flags.</param>
        /// <param name="sourceOf">For materials ported from another game: the export each was ported from, whose shipped shaders a stripped
        /// graph is recovered from. Null (or a null result) means the material's own shaders.</param>
        public List<MaterialRecompileOutcome> Recompile(IEnumerable<ExportEntry> materials,
            Func<ExportEntry, IEnumerable<LE3MaterialUsage>> requiredUses = null, IProgress<string> progress = null,
            CancellationToken cancellationToken = default, Func<ExportEntry, ExportEntry> sourceOf = null)
        {
            lock (gate)
            {
                currentSourceOf = sourceOf;
                try { return RecompileCore(materials, requiredUses, progress, cancellationToken, forceInstancesOfChangedBases: true); }
                finally { currentSourceOf = null; }
            }
        }

        private Func<ExportEntry, ExportEntry> currentSourceOf;

        /// <summary>
        /// For the lightmap bakers: makes <paramref name="material"/> (any material entry) drawable on a lightmapped static mesh, if it
        /// isn't already, by setting its base Material's bUsedWithStaticLighting and bUsedWithVectorLightMaps and recompiling it and
        /// its static-permutation instances in that package. The result is remembered per base Material.
        /// </summary>
        /// <param name="canModify">Whether a package may be changed (the bake's editable packages).</param>
        /// <returns>True if something was recompiled, so the caller should drop what it cached about shaders and check again.</returns>
        public bool TryEnableStaticLighting(IEntry material, Func<IMEPackage, bool> canModify, out string message)
        {
            message = null;
            if (material == null) return false;
            lock (gate)
            {
                var (baseMaterial, owner, chainError) = ResolveBase(material);
                if (baseMaterial == null) { message = chainError; return false; }
                if (!CanRecompile(baseMaterial.Game, out message, shaderDirectory)) return false;
                if (baseMaterial.GetProperty<EnumProperty>("LightingModel")?.Value.Name == "MLM_Unlit") return false;
                var outcomes = new List<MaterialRecompileOutcome>();

                // The base Material: needs the flags (the game checks them before drawing a lightmapped mesh) and their shaders.
                if (!staticLightingResults.TryGetValue(baseMaterial, out bool baseReady))
                {
                    var uses = LE3MaterialUsageAdvisor.LightmappedStaticMesh(simpleLightMaps: false);
                    baseReady = LE3MaterialUsageAdvisor.Check(baseMaterial.GetProperties(), uses, baseMaterial.ClassName).MissingFlags.Count == 0 &&
                                HasLightmapShaders(baseMaterial);
                    if (!baseReady)
                    {
                        if (!canModify(baseMaterial.FileRef))
                        {
                            message = $"{baseMaterial.InstancedFullPath} isn't compiled for static lighting, and it's in {Path.GetFileName(baseMaterial.FileRef.FilePath)}, which this bake doesn't change. Port it into the level's package to have it recompiled.";
                            staticLightingResults[baseMaterial] = false;
                            return false;
                        }
                        outcomes.AddRange(RecompileCore([baseMaterial], m => ReferenceEquals(m, baseMaterial) ? uses : [], null, CancellationToken.None,
                            forceInstancesOfChangedBases: true));
                        baseReady = outcomes.All(o => o.Success);
                    }
                    staticLightingResults[baseMaterial] = baseReady;
                }
                if (!baseReady)
                {
                    // Reported the first time only; later receivers of the same material stay quiet
                    message = outcomes.Count > 0 ? string.Join(Environment.NewLine, outcomes) : null;
                    return false;
                }

                // The static permutation the receiver draws with, if it has its own and still lacks the shaders.
                if (owner != null && !ReferenceEquals(owner, baseMaterial) && !failedOwners.Contains(owner) && !HasLightmapShaders(owner))
                {
                    if (!canModify(owner.FileRef))
                    {
                        message = $"{owner.InstancedFullPath} isn't compiled for static lighting, and it's in {Path.GetFileName(owner.FileRef.FilePath)}, which this bake doesn't change.";
                        failedOwners.Add(owner);
                        return false;
                    }
                    var ownerOutcomes = RecompileCore([owner], null, null, CancellationToken.None, forceInstancesOfChangedBases: false);
                    if (!ownerOutcomes.All(o => o.Success)) failedOwners.Add(owner);
                    outcomes.AddRange(ownerOutcomes);
                }
                message = string.Join(Environment.NewLine, outcomes);
                return outcomes.Count > 0 && outcomes.All(o => o.Success);
            }
        }

        /// <summary>
        /// Whether a Material, or a MaterialInstance with a static permutation, has a shader map (in its package or the game's
        /// RefShaderCache) with the base pass shaders of both LE3 lightmap kinds (texture and vertex) on static meshes. Not cached.
        /// </summary>
        public static bool HasLightmapShaders(ExportEntry material)
        {
            try
            {
                foreach (string policy in new[] { "FDirectionalLightMapTexturePolicy", "FDirectionalVertexLightMapPolicy" })
                {
                    var (map, shaders) = ShaderCacheManipulator.GetMaterialShaderMapAndShadersForVertexFactory(material, "FLocalVertexFactory",
                        $"TBasePassVertexShader{policy}FNoDensityPolicy", $"TBasePassPixelShader{policy}NoSkyLight");
                    if (map == null || shaders.Any(shader => shader == null)) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private List<MaterialRecompileOutcome> RecompileCore(IEnumerable<ExportEntry> materials, Func<ExportEntry, IEnumerable<LE3MaterialUsage>> requiredUses,
            IProgress<string> progress, CancellationToken cancellationToken, bool forceInstancesOfChangedBases)
        {
            var outcomes = new List<MaterialRecompileOutcome>();
            var bases = new List<ExportEntry>();
            var instances = new List<ExportEntry>();
            foreach (var material in materials.Distinct())
            {
                if (material.IsDefaultObject) continue;
                if (material.ClassName is "Material" or "DecalMaterial") bases.Add(material);
                else if (material.IsA("MaterialInstance") && material.GetProperty<BoolProperty>("bHasStaticPermutationResource")?.Value == true) instances.Add(material);
            }

            int total = bases.Count + instances.Count, done = 0;
            foreach (var baseMaterial in bases)
            {
                if (cancellationToken.IsCancellationRequested) break;
                progress?.Report($"Compiling shaders for {baseMaterial.ObjectName.Instanced} ({++done} of {total})");
                var outcome = RecompileBase(baseMaterial, requiredUses?.Invoke(baseMaterial) ?? [], cancellationToken);
                outcomes.Add(outcome);
                if (!outcome.Success || !forceInstancesOfChangedBases || outcome.FlagsSet.Count == 0) continue;
                // The base's flags changed what its static permutations need: recompile this package's ones too.
                foreach (var instance in baseMaterial.FileRef.Exports.Where(e => e.IsA("MaterialInstance") && !e.IsDefaultObject &&
                             e.GetProperty<BoolProperty>("bHasStaticPermutationResource")?.Value == true && !instances.Contains(e)))
                {
                    if (ReferenceEquals(ResolveBase(instance).Base, baseMaterial)) { instances.Add(instance); total++; }
                }
            }
            foreach (var instance in instances)
            {
                if (cancellationToken.IsCancellationRequested) break;
                progress?.Report($"Compiling shaders for {instance.ObjectName.Instanced} ({++done} of {total})");
                outcomes.Add(RecompileInstance(instance, cancellationToken));
            }
            return outcomes;
        }

        private MaterialRecompileOutcome RecompileBase(ExportEntry material, IEnumerable<LE3MaterialUsage> uses, CancellationToken cancellationToken)
        {
            if (!CanRecompile(material.Game, out string reason, shaderDirectory)) return new(material, false, reason, [], 0);
            var originalProperties = material.GetProperties();
            var report = LE3MaterialUsageAdvisor.Check(originalProperties, uses, material.ClassName);
            var flags = report.MissingFlags.ToList();
            string warning = report.Vetoed.Count > 0
                ? $" The game won't draw it as {string.Join(", ", report.Vetoed)} whatever its flags say ({(report.Vetoed.Count == 1 ? "it's" : "they're")} vetoed by bUsedWithDecals/bUsedWithFogVolumes)."
                : "";
            if (flags.Count > 0)
            {
                var properties = material.GetProperties();
                foreach (string flag in flags) properties.AddOrReplaceProp(new BoolProperty(true, flag));
                material.WriteProperties(properties);
            }

            var cookedResource = ObjectBinary.From<Material>(material).SM3MaterialResource;
            var request = new MaterialShaderCompileRequest
            {
                Material = material,
                Resource = cookedResource,
                ResourceId = Guid.NewGuid(),
                FriendlyName = material.ObjectName.Instanced,
                SourceProvider = GetSources,
                GetTextureMetadata = GetTextureMetadata,
                GetFontTexturePages = GetFontTexturePages,
            };
            // A stripped graph (the usual cooked case) is recovered from the shaders the game shipped for it
            MaterialRecompileOutcome outcome = null;
            if (StrippedGraphProblem(material) == null)
            {
                outcome = CompileAndInstall(request, null, cancellationToken, flags);
                // Partly stripped (found once compiled): recover instead
                if (!outcome.Success && outcome.Message?.Contains("stripped") == true) outcome = null;
            }
            outcome ??= CompileRecovered(request, null, material, material, cookedResource, cancellationToken, flags);
            if (!outcome.Success && flags.Count > 0) material.WriteProperties(originalProperties);
            return warning.Length == 0 ? outcome : outcome with { Message = outcome.Message + warning };
        }

        private MaterialRecompileOutcome RecompileInstance(ExportEntry instance, CancellationToken cancellationToken)
        {
            if (!CanRecompile(instance.Game, out string reason, shaderDirectory)) return new(instance, false, reason, [], 0);
            var newId = Guid.NewGuid();
            var request = new MaterialShaderCompileRequest
            {
                Material = instance,
                ResourceId = newId,
                FriendlyName = instance.ObjectName.Instanced,
                SourceProvider = GetSources,
                GetTextureMetadata = GetTextureMetadata,
                GetFontTexturePages = GetFontTexturePages,
            };
            var resolver = new RekeyingInstanceResolver(new LE3MaterialInstanceResolver(ResolveImport), newId);
            var baseMaterial = ResolveBase(instance).Base;
            if (baseMaterial != null && StrippedGraphProblem(baseMaterial) == null)
            {
                var outcome = CompileAndInstall(request, resolver, cancellationToken, []);
                if (outcome.Success || outcome.Message?.Contains("stripped") != true) return outcome;
            }
            if (baseMaterial == null) return new(instance, false, "its base material couldn't be found.", [], 0);
            var cookedResource = ObjectBinary.From<MaterialInstance>(instance).SM3StaticPermutationResource;
            return CompileRecovered(request, resolver, instance, baseMaterial, cookedResource, cancellationToken, []);
        }

        /// <summary>
        /// Compiles a material whose cooked graph is stripped from code recovered from the shaders its game shipped for it (the export it
        /// was ported from, or its own), then checks every recompiled static-mesh pixel shader against the corresponding source shader before
        /// installing. The main base pass must reproduce the source's; others that don't are reported.
        /// </summary>
        private MaterialRecompileOutcome CompileRecovered(MaterialShaderCompileRequest request, IMaterialShaderInstanceResolver resolver,
            ExportEntry owner, ExportEntry baseMaterial, MaterialResource cookedResource, CancellationToken cancellationToken, IReadOnlyList<string> flags)
        {
            var source = currentSourceOf?.Invoke(owner) ?? owner;
            var (map, shaders, error) = SourceShaders(source);
            if (map == null) return new(owner, false, $"its graph was stripped when it was cooked, and {error}. Nothing was installed.", flags, 0);
            LegendaryExplorerCore.Shaders.Recovery.RecoveredMaterialCode code;
            try
            {
                code = LegendaryExplorerCore.Shaders.Recovery.MaterialCodeRecovery.Recover(owner, map, cookedResource, shaders, baseMaterial);
            }
            catch (LegendaryExplorerCore.Shaders.Recovery.ShaderRecoveryException e)
            {
                return new(owner, false, $"its graph was stripped when it was cooked, and its code couldn't be recovered from {source.Game}'s shaders: {e.Message} Nothing was installed.", flags, 0);
            }
            var recoveredRequest = request with { TranslationOverride = (compiler, sources) => compiler.TranslateRecoveredMaterial(sources, code) };
            return CompileAndInstall(recoveredRequest, resolver, cancellationToken, flags, shaders, source.Game);
        }

        /// <summary>The shader map an export's game shipped for it, and a lookup of its shaders' bytecode (its package's cache, then the game's).</summary>
        private static (MaterialShaderMap Map, Func<string, string, byte[]> Shaders, string Error) SourceShaders(ExportEntry source)
        {
            MaterialShaderMap map;
            try { (map, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(source.FileRef, ShaderCacheManipulator.GetShaderMapId(source), null, true, []); }
            catch (Exception e) { return (null, null, $"its {source.Game} shader map couldn't be read ({e.Message})"); }
            if (map == null) return (null, null, $"{source.Game} has no shader map for {source.InstancedFullPath} to recover it from");
            var local = source.FileRef.FindExport("SeekFreeShaderCache", "ShaderCache") is { } cacheExport ? ObjectBinary.From<ShaderCache>(cacheExport) : null;
            byte[] Get(string vertexFactory, string type)
            {
                var reference = map.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == vertexFactory)?.Shaders.FirstOrDefault(s => s.Key.Name == type).Value;
                if (reference == null) return null;
                if (local != null && local.Shaders.TryGetValue(reference.Id, out var shader)) return shader.ShaderByteCode;
                return RefShaderCacheReader.GetShaders(source.Game, [reference.Id], out _, out _)?.FirstOrDefault()?.ShaderByteCode;
            }
            return (map, Get, null);
        }

        /// <summary>
        /// How many of the compiled static-mesh pixel shaders compute exactly what the source's do (<see cref="LegendaryExplorerCore.Shaders.Recovery.ShaderEquivalence"/>),
        /// and which don't. Shaders the source has none of, or that can't be analysed, aren't counted.
        /// </summary>
        private static (int Compared, int Equivalent, List<string> Different, bool MainPassEquivalent) VerifyAgainstSource(
            MaterialShaderCompileResult result, Func<string, string, byte[]> sourceShaders)
        {
            var map = result.Fragment.Cache.MaterialShaderMaps.Values.Single();
            var mesh = map.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType.Name == LegendaryExplorerCore.Shaders.Recovery.MaterialCodeRecovery.VertexFactory);
            var pairs = (mesh?.Shaders.Where(s => s.Key.Name.Contains("PixelShader", StringComparison.Ordinal)) ?? [])
                .Select(s => (Type: s.Key.Name, Ours: result.Fragment.Cache.Shaders.TryGetValue(s.Value.Id, out var shader) ? shader.ShaderByteCode : null,
                              Source: sourceShaders(LegendaryExplorerCore.Shaders.Recovery.MaterialCodeRecovery.VertexFactory, s.Key.Name)))
                .Where(p => p.Ours != null && p.Source != null).ToList();
            var outcomes = new System.Collections.Concurrent.ConcurrentBag<(string Type, bool? Equivalent)>();
            Parallel.ForEach(pairs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, pair =>
            {
                try { outcomes.Add((pair.Type, LegendaryExplorerCore.Shaders.Recovery.ShaderEquivalence.Compare(pair.Source, pair.Ours, trials: 24).Equivalent)); }
                catch (LegendaryExplorerCore.Shaders.Recovery.ShaderRecoveryException) { outcomes.Add((pair.Type, null)); }
            });
            var compared = outcomes.Where(o => o.Equivalent != null).ToList();
            bool mainPass = compared.Where(o => o.Type is LegendaryExplorerCore.Shaders.Recovery.MaterialCodeRecovery.UnlitShader).All(o => o.Equivalent == true);
            return (compared.Count, compared.Count(o => o.Equivalent == true), compared.Where(o => o.Equivalent == false).Select(o => o.Type).OrderBy(t => t).ToList(), mainPass);
        }

        private static readonly string[] MaterialInputs =
        [
            "DiffuseColor", "EmissiveColor", "Normal", "SpecularColor", "SpecularPower", "Opacity", "OpacityMask", "Distortion",
            "CustomLighting", "CustomLightingDiffuse", "WorldPositionOffset", "DiffusePower", "AnisotropicDirection", "TwoSidedLightingMask"
        ];

        private const string StrippedAdvice = "Cooking keeps only a material's parameter nodes in most cooked LE1, LE2 and LE3 packages, so the graph the game " +
                                              "compiled is gone and compiling what's left would draw it black. Nothing was installed.";

        /// <summary>
        /// Why a base Material's graph is visibly stripped, or null: nodes nulled out of Expressions (how LE2's cooker leaves it), or no input
        /// connected to anything (LE1 and LE3 compact the array instead). A graph that passes can still be partly stripped; see
        /// <see cref="MissingCookedTexturesProblem"/>, which checks the compiled result against what the cooked resource recorded.
        /// </summary>
        public static string StrippedGraphProblem(ExportEntry baseMaterial)
        {
            var expressions = baseMaterial.GetProperty<ArrayProperty<ObjectProperty>>("Expressions");
            int nulled = expressions?.Count(e => e.Value == 0) ?? 0;
            if (nulled > 0) return $"its graph was stripped when it was cooked ({nulled} of {expressions.Count} nodes are gone). {StrippedAdvice}";
            var properties = baseMaterial.GetProperties();
            bool anyInput = MaterialInputs.Any(input => properties.GetProp<StructProperty>(input)?.GetProp<ObjectProperty>("Expression")?.Value is { } v && v != 0);
            return anyInput ? null : $"none of its inputs are connected: its graph was stripped when it was cooked. {StrippedAdvice}";
        }

        /// <summary>
        /// The cooked resource lists every texture the game's compile of the real graph sampled. If our compile of what's left samples fewer,
        /// the graph was partly stripped and the result would be missing those textures (usually drawing black): why, or null.
        /// </summary>
        /// <summary>
        /// When the material already has a shader map in this game (its package's or the RefShaderCache), that map is the game's compile of
        /// the real graph: every base pass pixel shader ours shares with it must have the same instruction count, or what's left of the graph
        /// isn't what the game compiled. Why not, or null (also when there's no map to compare with, as after a cross-game port).
        /// </summary>
        public static string ExistingShadersProblem(MaterialShaderCompileResult result, ExportEntry owner)
        {
            MaterialShaderMap existing;
            try { (existing, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(owner.FileRef, ShaderCacheManipulator.GetShaderMapId(owner), null, true, []); }
            catch (Exception) { return null; }
            if (existing == null) return null;
            var ours = result.Fragment.Cache.MaterialShaderMaps.Values.Single();
            var local = owner.FileRef.FindExport("SeekFreeShaderCache", "ShaderCache") is { } cacheExport ? ObjectBinary.From<ShaderCache>(cacheExport) : null;
            var differences = new List<string>();
            int compared = 0;
            foreach (var existingMesh in existing.MeshShaderMaps)
            {
                var ourMesh = ours.MeshShaderMaps.FirstOrDefault(m => m.VertexFactoryType == existingMesh.VertexFactoryType);
                if (ourMesh == null) continue;
                foreach (var (type, existingRef) in existingMesh.Shaders.Where(s => s.Key.Name.StartsWith("TBasePassPixelShader", StringComparison.Ordinal)))
                {
                    var ourRef = ourMesh.Shaders.FirstOrDefault(s => s.Key == type).Value;
                    if (ourRef == null || !result.Fragment.Cache.Shaders.TryGetValue(ourRef.Id, out var ourShader)) continue;
                    Shader existingShader = local != null && local.Shaders.TryGetValue(existingRef.Id, out var l) ? l
                        : RefShaderCacheReader.GetShaders(owner.Game, [existingRef.Id], out _, out _)?.FirstOrDefault();
                    if (existingShader == null) continue;
                    compared++;
                    if (existingShader.InstructionCount != ourShader.InstructionCount)
                        differences.Add($"{type.Name} ({existingMesh.VertexFactoryType.Name}): {existingShader.InstructionCount} vs {ourShader.InstructionCount} instructions");
                    if (compared >= 24) break;
                }
                if (compared >= 24) break;
            }
            return differences.Count == 0 ? null
                : $"its graph was partly stripped when it was cooked: it doesn't compile to the shaders the game has for it ({differences[0]}" +
                  $"{(differences.Count > 1 ? $", and {differences.Count - 1} more" : "")}). {StrippedAdvice}";
        }

        public static string MissingCookedTexturesProblem(MaterialShaderCompileResult result, ExportEntry owner)
        {
            var cooked = owner.ClassName is "Material" or "DecalMaterial"
                ? ObjectBinary.From<Material>(owner).SM3MaterialResource?.UniformExpressionTextures
                : ObjectBinary.From<MaterialInstance>(owner).SM3StaticPermutationResource?.UniformExpressionTextures;
            if (cooked == null || cooked.Length == 0) return null;
            var basePackage = result.Translation.CompilationInput.BaseMaterial.FileRef;
            var compiled = result.Translation.UniformExpressionTextures.Select(u => basePackage.GetEntry(u)?.InstancedFullPath)
                .Where(p => p != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // An instance's resource also lists its texture parameter values, which don't come from the graph
            var instanceValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in result.InstanceResolution?.Chain ?? [])
                foreach (var value in (IEnumerable<StructProperty>)link.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues") ?? [])
                    if (value.GetProp<ObjectProperty>("ParameterValue")?.ResolveToEntry(link.FileRef) is { } texture) instanceValues.Add(texture.InstancedFullPath);
            var missing = cooked.Select(u => owner.FileRef.GetEntry(u)?.InstancedFullPath).Where(p => p != null && !compiled.Contains(p) && !instanceValues.Contains(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return missing.Count == 0 ? null
                : $"its graph was partly stripped when it was cooked: the game's compile sampled {missing.Count} texture{(missing.Count == 1 ? "" : "s")} " +
                  $"({string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? ", ..." : "")}) that what's left of it doesn't. {StrippedAdvice}";
        }

        private MaterialRecompileOutcome CompileAndInstall(MaterialShaderCompileRequest request, IMaterialShaderInstanceResolver resolver,
            CancellationToken cancellationToken, IReadOnlyList<string> flags, Func<string, string, byte[]> recoveredFrom = null, MEGame sourceGame = MEGame.Unknown)
        {
            var material = request.Material;
            var service = new MaterialShaderCompilationService(new MaterialShaderCompilationOptions
            {
                Enabled = true,
                InstanceResolver = resolver,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            });
            MaterialShaderCompileResult result;
            try
            {
                result = service.CompileMaterialAsync(request, null, cancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                return new(material, false, $"Compiling failed: {e.Message}", flags, 0);
            }
            if (result.Outcome != MaterialShaderCompileOutcome.SucceededFragment)
            {
                string errors = string.Join(" ", result.Errors.Take(3).Select(e => e.Trim()));
                return new(material, false, $"Couldn't compile its shaders ({result.Outcome} at {result.Phase}{(result.FailedTarget != null ? $", {result.FailedTarget}" : "")}): {errors}", flags, 0);
            }
            if (!result.IsCompleteShaderSet)
                return new(material, false, "The compiler couldn't produce every shader the game needs for it, so nothing was installed.", flags, 0);
            string verification = null;
            if (recoveredFrom == null)
            {
                if (MissingCookedTexturesProblem(result, result.InstanceResolution?.PermutationOwner ?? material) is { } partlyStripped)
                    return new(material, false, partlyStripped, flags, 0);
                if (ExistingShadersProblem(result, result.InstanceResolution?.PermutationOwner ?? material) is { } differs)
                    return new(material, false, differs, flags, 0);
            }
            else
            {
                // Recovered code: the recompiled shaders must compute what the source game's shaders do
                var (compared, equivalent, different, mainPass) = VerifyAgainstSource(result, recoveredFrom);
                if (!mainPass)
                    return new(material, false, $"its code was recovered from {sourceGame}'s shaders, but the recompiled base pass doesn't reproduce {sourceGame}'s, so nothing was installed.", flags, 0);
                verification = $" Recovered from {sourceGame}'s shaders: {equivalent} of {compared} pixel shaders compute exactly what {sourceGame}'s do" +
                               (different.Count > 0 ? $" (differ: {string.Join(", ", different.Take(4))}{(different.Count > 4 ? ", ..." : "")})." : ".");
            }
            try
            {
                installer.Install(result, material.FileRef);
            }
            catch (Exception e)
            {
                return new(material, false, $"Couldn't install its shaders: {e.Message}", flags, 0);
            }
            return new(material, true, verification, flags, result.Fragment.Cache.Shaders.Count);
        }

        /// <summary>The instance resolver, with the resolved static key moved to a fresh ID so the new map gets its own key.</summary>
        private sealed class RekeyingInstanceResolver(IMaterialShaderInstanceResolver inner, Guid newId) : IMaterialShaderInstanceResolver
        {
            public MaterialInstanceResolution Resolve(ExportEntry materialInstance)
            {
                var resolution = inner.Resolve(materialInstance);
                if (resolution is not { Success: true, EffectiveStaticParameters: { } parameters }) return resolution;
                var rekeyed = new StaticParameterSet
                {
                    BaseMaterialId = newId,
                    StaticSwitchParameters = parameters.StaticSwitchParameters,
                    StaticComponentMaskParameters = parameters.StaticComponentMaskParameters,
                    NormalParameters = parameters.NormalParameters,
                    TerrainWeightParameters = parameters.TerrainWeightParameters,
                };
                return new MaterialInstanceResolution
                {
                    RequestedInstance = resolution.RequestedInstance, Chain = resolution.Chain, BaseMaterial = resolution.BaseMaterial,
                    PermutationOwner = resolution.PermutationOwner, EffectiveStaticParameters = rekeyed,
                    PermutationResource = resolution.PermutationResource, Warnings = resolution.Warnings
                };
            }
        }

        private LE3ShaderSourceSnapshot GetSources() =>
            Sources.GetOrAdd(shaderDirectory, dir => new Lazy<LE3ShaderSourceSnapshot>(() => LE3ShaderSourceSnapshot.FromDirectory(dir, 1252))).Value;

        private ExportEntry ResolveImport(ImportEntry import)
        {
            lock (cache) return EntryImporter.TryResolveImport(import, out var export, cache) ? export : null;
        }

        /// <summary>The base Material at the end of an entry's instance chain, and the first static-permutation owner on the way (or the base).</summary>
        private (ExportEntry Base, ExportEntry Owner, string Error) ResolveBase(IEntry material)
        {
            IEntry current = material;
            ExportEntry owner = null;
            for (int depth = 0; depth < 64 && current != null; depth++)
            {
                var export = current as ExportEntry ?? ResolveImport((ImportEntry)current);
                if (export == null) return (null, null, $"{current.InstancedFullPath} couldn't be found.");
                if (export.ClassName is "Material" or "DecalMaterial") return (export, owner ?? export, null);
                var properties = export.GetProperties();
                if (owner == null && properties.GetProp<BoolProperty>("bHasStaticPermutationResource")?.Value == true) owner = export;
                string parentProperty = export.IsA("MaterialInstance") ? "Parent" : export.ClassName == "RvrEffectsMaterialUser" ? "m_pBaseMaterial" : null;
                int parent = parentProperty == null ? 0 : properties.GetProp<ObjectProperty>(parentProperty)?.Value ?? 0;
                if (parent == 0) return (null, null, $"{export.InstancedFullPath} has no base material.");
                current = export.FileRef.GetEntry(parent);
            }
            return (null, null, $"{material.InstancedFullPath}'s parent chain doesn't end in a Material.");
        }

        private Compiler.TextureCompilationMetadata GetTextureMetadata(IEntry entry)
        {
            var texture = entry as ExportEntry ?? (entry is ImportEntry import ? ResolveImport(import) : null);
            if (texture == null) return null;
            bool cube = texture.IsA("TextureCube");
            var dimension = cube ? Compiler.TextureDimension.TextureCube : Compiler.TextureDimension.Texture2D;
            // Render targets, movies and the like have no compression or unpack range to read.
            if (!cube && !texture.IsA("Texture2D")) return new Compiler.TextureCompilationMetadata(dimension);
            var properties = texture.GetProperties();
            Vector2? flipBookScale = null;
            if (texture.IsA("TextureFlipBook"))
            {
                // The game derives it on load: 1 / image count on each axis.
                static float Count(Property p) => p switch { IntProperty i => i.Value, ByteProperty b => b.Value, FloatProperty f => f.Value, _ => 1 };
                flipBookScale = new Vector2(1f / Count(properties.GetProp<Property>("HorizontalImages")), 1f / Count(properties.GetProp<Property>("VerticalImages")));
            }
            return Compiler.TextureCompilationMetadata.FromProperties(dimension, properties, flipBookScale);
        }

        /// <summary>A font's atlas pages, when the font is in the same package as the material (the compiler needs them in its package).</summary>
        private static IReadOnlyList<IEntry> GetFontTexturePages(IEntry font)
        {
            if (font is not ExportEntry export) return null;
            return export.GetProperty<ArrayProperty<ObjectProperty>>("Textures")?.Select(t => t.Value == 0 ? null : export.FileRef.GetEntry(t.Value)).ToArray();
        }
    }
}
