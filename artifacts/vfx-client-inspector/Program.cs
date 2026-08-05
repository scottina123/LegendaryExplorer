using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using BinaryPack;
using System.IO;
using System.IO.Compression;

if (args.Length >= 3 && args[0].Equals("package", StringComparison.OrdinalIgnoreCase))
{
    LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);
    using IMEPackage package = MEPackageHandler.OpenMEPackage(args[1]);
    int uIndex = int.Parse(args[2]);
    ExportEntry export = package.GetUExport(uIndex);
    using var directCache = new PackageCache();
    var renderContext = new MeshRenderContext();
    VfxPreviewDefinition definition = new ParticleSystemSourceAdapter().CreateDefinition(export, directCache);
    Console.WriteLine($"VFX {export.ClassName} {export.InstancedFullPath} emitters={definition.Emitters.Count}");
    foreach (VfxEmitterDefinition emitter in definition.Emitters)
    {
        Console.WriteLine($"EMITTER {emitter.Name} mode={emitter.RenderMode} material={emitter.Material?.ClassName} {emitter.Material?.InstancedFullPath}");
        IEntry current = emitter.Material;
        for (int depth = 0; depth < 8 && current is not null; depth++)
        {
            ExportEntry material = current switch
            {
                ExportEntry local => local,
                ImportEntry import => TryResolve(import, directCache),
                _ => null
            };
            if (material is null)
            {
                Console.WriteLine($"  {depth}: unresolved {current.ClassName} {current.InstancedFullPath}");
                break;
            }
            PropertyCollection props = material.GetProperties(packageCache: directCache);
            bool hasStatic = props.GetProp<BoolProperty>("bHasStaticPermutationResource")?.Value == true;
            Console.WriteLine($"  {depth}: {material.FileRef.FilePath}|{material.ClassName}|{material.InstancedFullPath}|static={hasStatic}|datasize={material.DataSize}");
            try
            {
                bool subUv = emitter.SubImagesHorizontal > 1 || emitter.SubImagesVertical > 1
                    || emitter.SubUVInterpolation != VfxSubUVInterpolation.None;
                bool dynamic = emitter.ParticleMaterial.UsesDynamicParameter;
                string factory = (subUv, dynamic) switch
                {
                    (false, false) => "FParticleVertexFactory",
                    (true, false) => "FParticleSubUVVertexFactory",
                    (false, true) => "FParticleDynamicParameterVertexFactory",
                    _ => "FParticleSubUVDynamicParameterVertexFactory"
                };
                (MaterialShaderMap map, Shader[] shaders) = ShaderCacheManipulator.GetMaterialShaderMapAndShadersForVertexFactory(
                    material, factory,
                    "TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy",
                    "TBasePassPixelShaderFNoLightMapPolicySkyLight",
                    "TBasePassPixelShaderFNoLightMapPolicyNoSkyLight");
                Console.WriteLine($"    {factory}: map={map is not null}, shaders={string.Join(',', shaders.Select(shader => shader is not null))}");
                foreach (MeshShaderMap meshMap in map?.MeshShaderMaps ?? [])
                {
                    string[] basePass = meshMap.Shaders.Keys.Select(name => name.Instanced)
                        .Where(name => name.StartsWith("TBasePass", StringComparison.Ordinal)).ToArray();
                    if (basePass.Length > 0)
                    {
                        Console.WriteLine($"      AVAILABLE {meshMap.VertexFactoryType.Instanced}: {string.Join(',', basePass)}");
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"    shader error: {exception}");
            }
            current = props.GetProp<ObjectProperty>("Parent")?.ResolveToEntry(material.FileRef);
        }
        ExportEntry selectedMaterial = emitter.Material switch
        {
            ExportEntry local => local,
            ImportEntry import => TryResolve(import, renderContext.PackageCache),
            _ => null
        };
        if (selectedMaterial is not null && emitter.RenderMode == VfxEmitterRenderMode.Sprite)
        {
            ExportEntry baseMaterial = selectedMaterial;
            var visited = new HashSet<string>();
            while (baseMaterial is not null && baseMaterial.ClassName != "Material"
                   && visited.Add($"{baseMaterial.FileRef.FilePath}|{baseMaterial.UIndex}"))
            {
                IEntry parent = baseMaterial.GetProperties(packageCache: renderContext.PackageCache)
                    .GetProp<ObjectProperty>("Parent")?.ResolveToEntry(baseMaterial.FileRef);
                baseMaterial = parent switch
                {
                    ExportEntry local => local,
                    ImportEntry import => TryResolve(import, renderContext.PackageCache),
                    _ => null
                };
            }
            emitter.ParticleMaterial.UsesDynamicParameter = baseMaterial?.ClassName == "Material"
                && ObjectBinary.From<Material>(baseMaterial).SM3MaterialResource.bUsesDynamicParameter;
            bool subUv = emitter.SubImagesHorizontal > 1 || emitter.SubImagesVertical > 1
                || emitter.SubUVInterpolation != VfxSubUVInterpolation.None;
            string selectedFactory = (subUv, emitter.ParticleMaterial.UsesDynamicParameter) switch
            {
                (false, false) => "FParticleVertexFactory",
                (true, false) => "FParticleSubUVVertexFactory",
                (false, true) => "FParticleDynamicParameterVertexFactory",
                _ => "FParticleSubUVDynamicParameterVertexFactory"
            };
            string alternateFactory = selectedFactory switch
            {
                "FParticleVertexFactory" => "FParticleDynamicParameterVertexFactory",
                "FParticleDynamicParameterVertexFactory" => "FParticleVertexFactory",
                "FParticleSubUVVertexFactory" => "FParticleSubUVDynamicParameterVertexFactory",
                _ => "FParticleSubUVVertexFactory"
            };
            foreach (string factoryCandidate in new[] { selectedFactory, alternateFactory })
            {
                try
                {
                    var proxy = new MaterialRenderProxy(renderContext, selectedMaterial, factoryCandidate);
                    Console.WriteLine($"  PROXY {factoryCandidate}: VS={proxy.UnrealVertexShader is not null} PS={proxy.UnrealPixelShader is not null} ownerVF={proxy.UnrealVertexShader?.VertexFactoryParameters.VertexFactoryType.Name}");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"  PROXY ERROR {factoryCandidate}: {exception}");
                }
            }
        }
    }
    return 0;
}

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: vfx-client-inspector <AssetDB.zip> <game-root> <name-filter>");
    return 2;
}

AssetDB database = LoadDatabase(args[0]);
LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default, objectDBsToLoad: [database.Game]);
string gameRoot = Path.GetFullPath(args[1]);
string filter = args[2];
bool concise = args.Length > 3 && args[3].Equals("concise", StringComparison.OrdinalIgnoreCase);
Dictionary<string, List<string>> filesByName = Directory.EnumerateFiles(gameRoot, "*.*", SearchOption.AllDirectories)
    .Where(path => path.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".upk", StringComparison.OrdinalIgnoreCase))
    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

using var cache = new PackageCache();
if (filter.StartsWith("mesh:", StringComparison.OrdinalIgnoreCase))
{
    string meshFilter = filter[5..];
    foreach (MeshRecord record in database.Meshes.Where(record =>
                 record.MeshName.Equals(meshFilter, StringComparison.OrdinalIgnoreCase)))
    {
        (string Path, MeshUsage Usage)? target = record.Usages
            .OrderBy(usage => usage.IsInMod)
            .Select(usage => (Path: ResolveMeshUsagePath(database, usage, filesByName), Usage: usage))
            .FirstOrDefault(candidate => candidate.Path is not null);
        if (target is not { Path: not null } found) continue;

        using IMEPackage package = MEPackageHandler.OpenMEPackage(found.Path);
        ExportEntry export = package.TryGetUExport(found.Usage.UIndex, out ExportEntry indexed)
            && indexed.ClassName == "SkeletalMesh" && indexed.ObjectName.Instanced == record.MeshName
                ? indexed
                : package.Exports.FirstOrDefault(candidate => candidate.ClassName == "SkeletalMesh"
                    && candidate.ObjectName.Instanced == record.MeshName);
        Console.WriteLine($"\n=== MESH {record.MeshName} | {found.Path} | #{export?.UIndex} ===");
        if (export is null) continue;
        var mesh = ObjectBinary.From<SkeletalMesh>(export);
        for (int materialIndex = 0; materialIndex < mesh.Materials.Length; materialIndex++)
        {
            IEntry materialEntry = export.FileRef.GetEntry(mesh.Materials[materialIndex]);
            ExportEntry material = materialEntry switch
            {
                ExportEntry local => local,
                ImportEntry import => TryResolve(import, cache),
                _ => null
            };
            Console.WriteLine($"MATERIAL[{materialIndex}] #{mesh.Materials[materialIndex]} {materialEntry?.ClassName} {materialEntry?.InstancedFullPath} => {material?.FileRef.FilePath}|{material?.ClassName}|{material?.InstancedFullPath}");
            if (material is not null)
            {
                foreach (string effectName in new[] { "Tears", "Tears_Asari" })
                {
                    ExportEntry effectMaterial = material.ResolveRvrEffectMaterial(effectName, cache);
                    if (effectMaterial is not null)
                    {
                        Console.WriteLine($"  RESOLVED {effectName} => {effectMaterial.ClassName} {effectMaterial.InstancedFullPath}");
                    }
                }
                if (!concise)
                {
                    DumpEntry(material, cache, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }
            }
        }
    }
    return 0;
}

if (filter.StartsWith("particle:", StringComparison.OrdinalIgnoreCase))
{
    string particleFilter = filter[9..];
    foreach (ParticleSysRecord record in database.Particles.Where(record =>
                 record.VFXType == ParticleSysRecord.VFXClass.ParticleSystem
                 && record.PSName.Contains(particleFilter, StringComparison.OrdinalIgnoreCase)))
    {
        (string Path, ParticleSysUsage Usage)? target = record.Usages
            .OrderBy(usage => usage.IsInMod)
            .ThenBy(usage => usage.IsInDLC)
            .Select(usage => (Path: ResolveUsagePath(database, usage, filesByName), Usage: usage))
            .FirstOrDefault(candidate => candidate.Path is not null);
        if (target is not { Path: not null } found) continue;

        using IMEPackage package = MEPackageHandler.OpenMEPackage(found.Path);
        ExportEntry export = package.TryGetUExport(found.Usage.UIndex, out ExportEntry indexed)
            && indexed.ClassName == "ParticleSystem" && indexed.ObjectName.Instanced == record.PSName
                ? indexed
                : package.Exports.FirstOrDefault(candidate => candidate.ClassName == "ParticleSystem"
                    && candidate.ObjectName.Instanced == record.PSName);
        Console.WriteLine($"\n=== PARTICLE {record.PSName} | {found.Path} | #{export?.UIndex} ===");
        if (export is null) continue;

        VfxPreviewDefinition definition = new ParticleSystemSourceAdapter().CreateDefinition(export, cache);
        foreach (VfxEmitterDefinition emitter in definition.Emitters)
        {
            ExportEntry material = emitter.Material switch
            {
                ExportEntry local => local,
                ImportEntry import => TryResolve(import, cache),
                _ => null
            };
            Console.WriteLine($"EMITTER {emitter.Name} mode={emitter.RenderMode} subuv={emitter.SubImagesHorizontal}x{emitter.SubImagesVertical} dynamic={emitter.DynamicParameters.Count} material={emitter.Material?.ClassName} {emitter.Material?.InstancedFullPath}");
            PrintMaterialSummary(material, cache);
        }
    }
    return 0;
}

foreach (ParticleSysRecord record in database.Particles.Where(record =>
             record.VFXType == ParticleSysRecord.VFXClass.RvrClientEffect
             && record.PSName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
{
    (string Path, ParticleSysUsage Usage)? target = record.Usages
        .OrderBy(usage => usage.IsInMod)
        .ThenBy(usage => usage.IsInDLC)
        .Select(usage => (Path: ResolveUsagePath(database, usage, filesByName), Usage: usage))
        .FirstOrDefault(candidate => candidate.Path is not null);
    if (target is not { Path: not null } found)
    {
        Console.WriteLine($"UNAVAILABLE {record.PSName}");
        continue;
    }

    using IMEPackage package = MEPackageHandler.OpenMEPackage(found.Path);
    ExportEntry export = package.TryGetUExport(found.Usage.UIndex, out ExportEntry indexed)
        && indexed.ClassName == "RvrClientEffect"
        && indexed.ObjectName.Instanced == record.PSName
            ? indexed
            : package.Exports.FirstOrDefault(candidate => candidate.ClassName == "RvrClientEffect"
                && candidate.ObjectName.Instanced == record.PSName);
    Console.WriteLine($"\n=== {record.PSName} | {found.Path} | #{export?.UIndex} ===");
    if (export is not null)
    {
        DumpEntry(export, cache, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
return 0;

static void DumpEntry(ExportEntry export, PackageCache cache, int depth, HashSet<string> visited)
{
    string key = $"{export.FileRef.FilePath}|{export.UIndex}";
    string indent = new(' ', depth * 2);
    if (!visited.Add(key))
    {
        Console.WriteLine($"{indent}* {export.ClassName} {export.InstancedFullPath} (seen)");
        return;
    }
    Console.WriteLine($"{indent}* {export.ClassName} {export.InstancedFullPath} #{export.UIndex}");
    foreach (Property property in export.GetProperties(packageCache: cache))
    {
        Console.WriteLine($"{indent}  {property.Name.Instanced} [{property.GetType().Name}] {FormatProperty(property, export.FileRef)}");
    }
    if (depth >= 5)
    {
        return;
    }
    foreach (IEntry reference in EnumerateReferences(export))
    {
        ExportEntry resolved = reference switch
        {
            ExportEntry child => child,
            ImportEntry import => TryResolve(import, cache),
            _ => null
        };
        Console.WriteLine($"{indent}  -> {reference.GetType().Name} {reference.ClassName} {reference.InstancedFullPath} => {resolved?.FileRef.FilePath}|{resolved?.InstancedFullPath}");
        if (resolved is not null)
        {
            DumpEntry(resolved, cache, depth + 1, visited);
        }
    }
}

static ExportEntry TryResolve(ImportEntry import, PackageCache cache)
{
    try { return EntryImporter.ResolveImport(import, cache); }
    catch { return null; }
}

static void PrintMaterialSummary(ExportEntry material, PackageCache cache)
{
    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    ExportEntry current = material;
    while (current is not null && visited.Add($"{current.FileRef.FilePath}|{current.UIndex}"))
    {
        PropertyCollection props = current.GetProperties(packageCache: cache);
        Console.WriteLine($"  MAT {current.ClassName} {current.InstancedFullPath} Blend={props.GetProp<EnumProperty>("BlendMode")?.Value.Instanced} Lighting={props.GetProp<EnumProperty>("LightingModel")?.Value.Instanced}");
        if (current.ClassName == "Material")
        {
            Material binary = ObjectBinary.From<Material>(current);
            Console.WriteLine($"    usesDynamic={binary.SM3MaterialResource.bUsesDynamicParameter}");
            foreach (int textureIndex in binary.SM3MaterialResource.UniformExpressionTextures)
            {
                IEntry texture = current.FileRef.GetEntry(textureIndex);
                Console.WriteLine($"    UNIFORM_TEX #{textureIndex} {texture?.ClassName} {texture?.InstancedFullPath}");
                if (texture is ExportEntry textureExport)
                {
                    PrintTextureSummary(textureExport);
                }
            }
            foreach (string inputName in new[] { "DiffuseColor", "EmissiveColor", "Opacity", "OpacityMask" })
            {
                if (props.GetProp<StructProperty>(inputName) is { } input)
                {
                    Console.WriteLine($"    INPUT {inputName} {FormatProperty(input, current.FileRef)}");
                }
            }
            if (props.GetProp<ArrayProperty<ObjectProperty>>("Expressions") is { } expressions)
            {
                foreach (ObjectProperty expressionReference in expressions)
                {
                    ExportEntry expression = expressionReference.ResolveToEntry(current.FileRef) switch
                    {
                        ExportEntry local => local,
                        ImportEntry import => TryResolve(import, cache),
                        _ => null
                    };
                    if (expression is null) continue;
                    PropertyCollection expressionProps = expression.GetProperties(packageCache: cache);
                    IEntry texture = expressionProps.GetProp<ObjectProperty>("Texture")?.ResolveToEntry(expression.FileRef);
                    Console.WriteLine($"    EXPR #{expression.UIndex} {expression.ClassName} param={expressionProps.GetProp<NameProperty>("ParameterName")?.Value.Instanced} texture={texture?.ClassName} {texture?.InstancedFullPath}");
                }
            }
            PrintShaderMapSummary(current);
            break;
        }

        IEntry parent = props.GetProp<ObjectProperty>("Parent")?.ResolveToEntry(current.FileRef);
        current = parent switch
        {
            ExportEntry local => local,
            ImportEntry import => TryResolve(import, cache),
            _ => null
        };
    }
}

static void PrintShaderMapSummary(ExportEntry material)
{
    StaticParameterSet parameters = (StaticParameterSet)ObjectBinary.From<Material>(material).SM3MaterialResource.ID;
    MaterialShaderMap shaderMap = null;
    ShaderCache localShaderCache = null;
    if (material.FileRef.FindExport("SeekFreeShaderCache", "ShaderCache") is { } seekFreeExport)
    {
        localShaderCache = ObjectBinary.From<ShaderCache>(seekFreeExport);
        localShaderCache.MaterialShaderMaps.TryGetValue(parameters, out shaderMap);
    }
    shaderMap ??= RefShaderCacheReader.GetMaterialShaderMap(material.Game, parameters, out _);
    foreach (MeshShaderMap meshMap in shaderMap?.MeshShaderMaps ?? [])
    {
        string[] basePassShaders = meshMap.Shaders.Keys
            .Select(key => key.Instanced)
            .Where(name => name.StartsWith("TBasePass", StringComparison.Ordinal))
            .ToArray();
        if (basePassShaders.Length > 0)
        {
            Console.WriteLine($"    VF {meshMap.VertexFactoryType.Instanced}: {string.Join(", ", basePassShaders)}");
            if (meshMap.VertexFactoryType.Instanced.StartsWith("FParticle", StringComparison.Ordinal)
                && meshMap.Shaders.TryGetValue("TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy", out ShaderReference vertexReference))
            {
                Shader vertexShader = null;
                if (localShaderCache?.Shaders.TryGetValue(vertexReference.Id, out Shader localShader) == true)
                {
                    vertexShader = localShader;
                }
                else
                {
                    vertexShader = RefShaderCacheReader.GetShaders(material.Game, [vertexReference.Id], out _, out _)?.FirstOrDefault();
                }
                PrintShaderSignature($"VS_{meshMap.VertexFactoryType.Instanced}", vertexShader?.ShaderByteCode);
            }
        }
    }
}

static void PrintShaderSignature(string label, byte[] byteCode)
{
    if (byteCode is null) return;
    using var reflection = new SharpDX.D3DCompiler.ShaderReflection(byteCode);
    var description = reflection.Description;
    var inputs = new List<string>();
    for (int index = 0; index < description.InputParameters; index++)
    {
        var parameter = reflection.GetInputParameterDescription(index);
        inputs.Add($"{parameter.SemanticName}{parameter.SemanticIndex}:r{parameter.Register}");
    }
    var outputs = new List<string>();
    for (int index = 0; index < description.OutputParameters; index++)
    {
        var parameter = reflection.GetOutputParameterDescription(index);
        outputs.Add($"{parameter.SemanticName}{parameter.SemanticIndex}:r{parameter.Register}");
    }
    Console.WriteLine($"      {label}_INPUT {string.Join(", ", inputs)}");
    Console.WriteLine($"      {label}_OUTPUT {string.Join(", ", outputs)}");
    if (label.Contains("DynamicParameter", StringComparison.Ordinal)
        || label.Contains("SubUV", StringComparison.Ordinal)
        || label.Contains("BeamTrail", StringComparison.Ordinal)
        || label.Contains("InstancedMesh", StringComparison.Ordinal))
    {
        using var shaderBytecode = new SharpDX.D3DCompiler.ShaderBytecode(byteCode);
        Console.WriteLine($"      {label}_DISASM_START");
        Console.WriteLine(shaderBytecode.Disassemble());
        Console.WriteLine($"      {label}_DISASM_END");
    }
}

static void PrintTextureSummary(ExportEntry texture)
{
    string format = texture.GetProperty<EnumProperty>("Format")?.Value.Instanced;
    int sizeX = texture.GetProperty<IntProperty>("SizeX")?.Value ?? 0;
    int sizeY = texture.GetProperty<IntProperty>("SizeY")?.Value ?? 0;
    try
    {
        var unrealTexture = new LegendaryExplorerCore.Unreal.Classes.Texture2D(texture);
        LegendaryExplorerCore.Textures.Image image = unrealTexture.ToImage(LegendaryExplorerCore.Textures.PixelFormat.ARGB);
        ReadOnlySpan<byte> pixels = image.mipMaps[0].data;
        int count = pixels.Length / 4;
        int alphaZero = 0;
        int alphaFull = 0;
        int dark = 0;
        long alphaSum = 0;
        for (int offset = 0; offset + 3 < pixels.Length; offset += 4)
        {
            byte alpha = pixels[offset + 3];
            alphaSum += alpha;
            if (alpha <= 2) alphaZero++;
            if (alpha >= 253) alphaFull++;
            if (pixels[offset] <= 8 && pixels[offset + 1] <= 8 && pixels[offset + 2] <= 8) dark++;
        }
        Console.WriteLine($"      TEX format={format} size={sizeX}x{sizeY} alphaZero={alphaZero * 100f / Math.Max(1, count):F1}% alphaFull={alphaFull * 100f / Math.Max(1, count):F1}% alphaAvg={alphaSum / (float)Math.Max(1, count):F1} dark={dark * 100f / Math.Max(1, count):F1}%");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"      TEX format={format} size={sizeX}x{sizeY} decode={exception.Message}");
    }
}

static IEnumerable<IEntry> EnumerateReferences(ExportEntry export)
{
    foreach (Property property in export.GetProperties())
    {
        if (property is ObjectProperty objectReference && objectReference.ResolveToEntry(export.FileRef) is { } objectEntry)
        {
            yield return objectEntry;
        }
        else if (property is ArrayProperty<ObjectProperty> references)
        {
            foreach (ObjectProperty arrayReference in references)
            {
                if (arrayReference.ResolveToEntry(export.FileRef) is { } arrayEntry)
                {
                    yield return arrayEntry;
                }
            }
        }
        else if (property is ArrayProperty<StructProperty> structures)
        {
            foreach (StructProperty item in structures)
            {
                foreach (IEntry structEntry in EnumerateStructReferences(item.Properties, export.FileRef))
                {
                    yield return structEntry;
                }
            }
        }
        else if (property is StructProperty structure)
        {
            foreach (IEntry structEntry in EnumerateStructReferences(structure.Properties, export.FileRef))
            {
                yield return structEntry;
            }
        }
    }
}

static IEnumerable<IEntry> EnumerateStructReferences(PropertyCollection properties, IMEPackage package)
{
    foreach (Property property in properties)
    {
        if (property is ObjectProperty objectReference && objectReference.ResolveToEntry(package) is { } objectEntry)
        {
            yield return objectEntry;
        }
        else if (property is StructProperty nested)
        {
            foreach (IEntry nestedEntry in EnumerateStructReferences(nested.Properties, package)) yield return nestedEntry;
        }
        else if (property is ArrayProperty<ObjectProperty> references)
        {
            foreach (ObjectProperty arrayReference in references)
                if (arrayReference.ResolveToEntry(package) is { } arrayEntry) yield return arrayEntry;
        }
    }
}

static string FormatProperty(Property property, IMEPackage package) => property switch
{
    ObjectProperty value => $"#{value.Value} {value.ResolveToEntry(package)?.ClassName} {value.ResolveToEntry(package)?.InstancedFullPath}",
    NameProperty value => value.Value.Instanced,
    EnumProperty value => value.Value.Instanced,
    BoolProperty value => value.Value.ToString(),
    FloatProperty value => value.Value.ToString("G9"),
    IntProperty value => value.Value.ToString(),
    ByteProperty value => value.Value.ToString(),
    StrProperty value => value.Value,
    ArrayProperty<ObjectProperty> value => string.Join(", ", value.Select(item => $"#{item.Value}:{item.ResolveToEntry(package)?.InstancedFullPath}")),
    ArrayProperty<StructProperty> value => string.Join(" | ", value.Select(item => $"{item.StructType} {{{string.Join(", ", item.Properties.Select(child => $"{child.Name.Instanced}={FormatProperty(child, package)}"))}}}")),
    StructProperty value => $"{value.StructType} {{{string.Join(", ", value.Properties.Select(child => $"{child.Name.Instanced}={FormatProperty(child, package)}"))}}}",
    _ => string.Empty
};

static AssetDB LoadDatabase(string archivePath)
{
    using ZipArchive archive = ZipFile.OpenRead(archivePath);
    ZipArchiveEntry entry = archive.Entries.Single(item => item.Name.StartsWith("MasterDB.", StringComparison.Ordinal));
    using var memory = new MemoryStream((int)entry.Length);
    using (Stream stream = entry.Open()) stream.CopyTo(memory);
    return BinaryConverter.Deserialize<AssetDB>(memory.GetBuffer().AsSpan(0, (int)memory.Length));
}

static string ResolveUsagePath(AssetDB database, ParticleSysUsage usage, Dictionary<string, List<string>> filesByName)
{
    FileNameDirKeyPair file = database.FileList[usage.FileKey];
    if (!filesByName.TryGetValue(file.FileName, out List<string> candidates)) return null;
    string contentDirectory = database.ContentDir[file.DirectoryKey];
    if (contentDirectory.Equals("BioGame", StringComparison.OrdinalIgnoreCase))
        return candidates.FirstOrDefault(path => !path.Contains($"{Path.DirectorySeparatorChar}DLC{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
    return candidates.FirstOrDefault(path => path.Contains(contentDirectory, StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
}

static string ResolveMeshUsagePath(AssetDB database, MeshUsage usage, Dictionary<string, List<string>> filesByName)
{
    FileNameDirKeyPair file = database.FileList[usage.FileKey];
    if (!filesByName.TryGetValue(file.FileName, out List<string> candidates)) return null;
    string contentDirectory = database.ContentDir[file.DirectoryKey];
    if (contentDirectory.Equals("BioGame", StringComparison.OrdinalIgnoreCase))
        return candidates.FirstOrDefault(path => !path.Contains($"{Path.DirectorySeparatorChar}DLC{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
    return candidates.FirstOrDefault(path => path.Contains(contentDirectory, StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
}
