using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

public sealed class VfxSourceAdapter : IVfxSourceAdapter
{
    private readonly ParticleSystemSourceAdapter particleSystemAdapter = new();
    private readonly WrappedVfxSourceAdapter wrappedAdapter = new();

    public bool CanAdapt(ExportEntry export) => particleSystemAdapter.CanAdapt(export) || wrappedAdapter.CanAdapt(export);

    public VfxPreviewDefinition CreateDefinition(ExportEntry export)
        => particleSystemAdapter.CanAdapt(export)
            ? particleSystemAdapter.CreateDefinition(export)
            : wrappedAdapter.CreateDefinition(export);
}

public sealed class ParticleSystemSourceAdapter : IVfxSourceAdapter
{
    private static readonly HashSet<string> MetadataProperties = new(StringComparer.Ordinal)
    {
        "LODValidity", "bEnabled", "Level", "PeakActiveParticles", "Modules", "RequiredModule", "SpawnModule", "TypeDataModule", "LODLevels", "Emitters"
    };

    private static readonly Dictionary<string, HashSet<string>> AppliedProperties = new(StringComparer.Ordinal)
    {
        ["ParticleSystem"] = new(StringComparer.Ordinal) { "FixedRelativeBoundingBox", "LODDistances", "LODSettings", "UpdateTime_Delta", "bUseFixedRelativeBoundingBox" },
        ["ParticleSpriteEmitter"] = new(StringComparer.Ordinal) { "EmitterName" },
        ["ParticleModuleRequired"] = new(StringComparer.Ordinal) { "SpawnRate", "BurstList", "Material", "EmitterDelay", "EmitterDuration", "EmitterLoops", "ScreenAlignment", "SubImages_Horizontal", "SubImages_Vertical", "InterpolationMethod", "bUseLocalSpace" },
        ["ParticleModuleSpawn"] = new(StringComparer.Ordinal) { "Rate", "RateScale", "BurstList", "bProcessSpawnRate", "bProcessBurstList" },
        ["ParticleModuleLifetime"] = new(StringComparer.Ordinal) { "Lifetime" },
        ["ParticleModuleLocation"] = new(StringComparer.Ordinal) { "StartLocation", "StartLocationRw" },
        ["ParticleModuleLocationDirect"] = new(StringComparer.Ordinal) { "Location", "LocationRw" },
        ["ParticleModuleLocationPrimitiveCylinder"] = new(StringComparer.Ordinal) { "StartLocation", "StartLocationRw", "StartRadius", "StartHeight", "VelocityScale", "HeightAxis", "bSurfaceOnly", "bVelocity", "bRadialVelocity", "Positive_X", "Positive_Y", "Positive_Z", "Negative_X", "Negative_Y", "Negative_Z" },
        ["ParticleModuleVelocity"] = new(StringComparer.Ordinal) { "StartVelocity", "StartVelocityRw" },
        ["ParticleModuleSize"] = new(StringComparer.Ordinal) { "StartSize", "StartSizeRw" },
        ["ParticleModuleInitialSize"] = new(StringComparer.Ordinal) { "StartSize", "StartSizeRw" },
        ["ParticleModuleSizeMultiplyLife"] = new(StringComparer.Ordinal) { "LifeMultiplier", "LifeMultiplierRw" },
        ["ParticleModuleColor"] = new(StringComparer.Ordinal) { "StartColor", "StartColorRw", "StartAlpha" },
        ["ParticleModuleColorOverLife"] = new(StringComparer.Ordinal) { "ColorOverLife", "ColorOverLifeRw", "AlphaOverLife" },
        ["ParticleModuleColorScaleOverLife"] = new(StringComparer.Ordinal) { "ColorScaleOverLifeRw", "AlphaScaleOverLife", "bEmitterTime" },
        ["ParticleModuleVelocityOverLifetime"] = new(StringComparer.Ordinal) { "VelOverLifeRw", "Absolute" },
        ["ParticleModuleAcceleration"] = new(StringComparer.Ordinal) { "AccelerationRw" },
        ["ParticleModuleAccelerationOverLifetime"] = new(StringComparer.Ordinal) { "AccelOverLifeRw" },
        ["ParticleModuleOrbit"] = new(StringComparer.Ordinal) { "OffsetAmountRw", "RotationAmountRw", "RotationRateAmountRw", "OffsetOptions", "RotationOptions", "RotationRateOptions" },
        ["ParticleModuleRotation"] = new(StringComparer.Ordinal) { "StartRotation" },
        ["ParticleModuleRotationRate"] = new(StringComparer.Ordinal) { "StartRotationRate" },
        ["ParticleModuleOrientationAxisLock"] = new(StringComparer.Ordinal) { "LockAxisFlags" },
        ["ParticleModuleSubUV"] = new(StringComparer.Ordinal) { "SubImageIndex" },
        ["ParticleModuleSubUVMovie"] = new(StringComparer.Ordinal) { "SubImageIndex", "FrameRate", "StartingFrame", "bUseEmitterTime" }
    };

    private static readonly HashSet<string> SupportedModules = new(StringComparer.Ordinal)
    {
        "ParticleModuleRequired",
        "ParticleModuleSpawn",
        "ParticleModuleLifetime",
        "ParticleModuleLocation",
        "ParticleModuleLocationDirect",
        "ParticleModuleLocationPrimitiveCylinder",
        "ParticleModuleVelocity",
        "ParticleModuleSize",
        "ParticleModuleInitialSize",
        "ParticleModuleSizeMultiplyLife",
        "ParticleModuleColor",
        "ParticleModuleColorOverLife",
        "ParticleModuleColorScaleOverLife",
        "ParticleModuleVelocityOverLifetime",
        "ParticleModuleAcceleration",
        "ParticleModuleAccelerationOverLifetime",
        "ParticleModuleOrbit",
        "ParticleModuleRotation",
        "ParticleModuleRotationRate",
        "ParticleModuleOrientationAxisLock",
        "ParticleModuleSubUV",
        "ParticleModuleSubUVMovie",
        "ParticleModuleUberLTISIVCL",
        "ParticleModuleUberLTISIVCLIL",
        "ParticleModuleUberLTISIVCLILIRSSBLIRR"
    };

    public bool CanAdapt(ExportEntry export) => export?.ClassName == "ParticleSystem";

    public VfxPreviewDefinition CreateDefinition(ExportEntry export)
    {
        IReadOnlyList<float> lodDistances = ReadLodDistances(export);
        IReadOnlyList<VfxLodSetting> lodSettings = ReadLodSettings(export);
        int selectedLodIndex = SelectPreviewLodIndex(lodDistances, lodSettings, null);
        VfxBounds? fixedLocalBounds = ReadFixedBounds(export);
        var definition = new VfxPreviewDefinition
        {
            Name = export.ObjectName.Instanced,
            SystemTransform = VfxPreviewDefinition.CreateUnitScaleCenteringTransform(fixedLocalBounds),
            LodDistances = lodDistances,
            LodSettings = lodSettings,
            SelectedLodIndex = selectedLodIndex,
            FixedLocalBounds = fixedLocalBounds
        };
        RecordPropertyCoverage(export, definition);
        var emitterRefs = export.GetProperty<ArrayProperty<ObjectProperty>>("Emitters");
        if (emitterRefs is null)
        {
            definition.Warnings.Add("The particle system has no Emitters array.");
            return definition;
        }
        foreach (ObjectProperty emitterRef in emitterRefs)
        {
            if (emitterRef.ResolveToEntry(export.FileRef) is not ExportEntry emitter)
            {
                continue;
            }
            RecordPropertyCoverage(emitter, definition);

            ExportEntry lod = SelectEmitterLod(emitter, selectedLodIndex);
            if (lod is null)
            {
                definition.Warnings.Add($"{emitter.ObjectName.Instanced}: no enabled LOD level was found.");
                continue;
            }

            RecordPropertyCoverage(lod, definition);
            foreach (ExportEntry child in EnumerateLodChildren(lod))
            {
                RecordPropertyCoverage(child, definition);
            }

            definition.Emitters.Add(ParseEmitter(emitter, lod, definition.Warnings));
        }

        if (definition.Emitters.Count == 0)
        {
            definition.Warnings.Add("No sprite emitters could be loaded from this particle system.");
        }

        return definition;
    }

    public static int SelectPreviewLodIndex(IReadOnlyList<float> lodDistances, IReadOnlyList<VfxLodSetting> lodSettings, float? cameraDistance)
    {
        int lodCount = Math.Max(lodDistances.Count, lodSettings.Count);
        if (lodCount == 0 || cameraDistance is null || !float.IsFinite(cameraDistance.Value))
        {
            return 0;
        }

        int selected = 0;
        for (int index = 0; index < lodDistances.Count; index++)
        {
            if (cameraDistance.Value >= lodDistances[index])
            {
                selected = index;
            }
        }
        return Math.Clamp(selected, 0, lodCount - 1);
    }

    private static ExportEntry SelectEmitterLod(ExportEntry emitter, int selectedLodIndex)
    {
        List<ExportEntry> lods = emitter.GetProperty<ArrayProperty<ObjectProperty>>("LODLevels")?
            .Select(reference => reference.ResolveToEntry(emitter.FileRef))
            .OfType<ExportEntry>()
            .ToList() ?? [];
        if (lods.Count == 0)
        {
            return null;
        }

        int clampedIndex = Math.Clamp(selectedLodIndex, 0, lods.Count - 1);
        ExportEntry selected = lods[clampedIndex];
        return selected.GetProperty<BoolProperty>("bEnabled")?.Value == false ? null : selected;
    }

    private static IReadOnlyList<float> ReadLodDistances(ExportEntry particleSystem) =>
        particleSystem.GetProperty<ArrayProperty<FloatProperty>>("LODDistances")?.Select(value => value.Value).ToList() ?? [];

    private static IReadOnlyList<VfxLodSetting> ReadLodSettings(ExportEntry particleSystem) =>
        particleSystem.GetProperty<ArrayProperty<StructProperty>>("LODSettings")?
            .Select(setting => new VfxLodSetting(setting.GetProp<BoolProperty>("bLit")?.Value == true))
            .ToList() ?? [];

    private static VfxBounds? ReadFixedBounds(ExportEntry particleSystem)
    {
        if (particleSystem.GetProperty<BoolProperty>("bUseFixedRelativeBoundingBox")?.Value != true
            || particleSystem.GetProperty<StructProperty>("FixedRelativeBoundingBox") is not { } box
            || !ReadBoxValidity(box))
        {
            return null;
        }

        var bounds = new VfxBounds(
            ReadVector(box.GetProp<StructProperty>("Min"), Vector3.Zero),
            ReadVector(box.GetProp<StructProperty>("Max"), Vector3.Zero));
        return bounds.IsValid ? bounds : null;
    }

    private static bool ReadBoxValidity(StructProperty box) =>
        box.Properties.FirstOrDefault(property => property.Name == "IsValid") switch
        {
            BioMask4Property mask => mask.Value != 0,
            ByteProperty value => value.Value != 0,
            BoolProperty value => value.Value,
            _ => false
        };

    private static IEnumerable<ExportEntry> EnumerateLodChildren(ExportEntry lod)
    {
        foreach (string propertyName in new[] { "RequiredModule", "SpawnModule", "TypeDataModule" })
        {
            if (lod.GetProperty<ObjectProperty>(propertyName)?.ResolveToEntry(lod.FileRef) is ExportEntry child)
            {
                yield return child;
            }
        }
        if (lod.GetProperty<ArrayProperty<ObjectProperty>>("Modules") is { } modules)
        {
            foreach (ObjectProperty moduleReference in modules)
            {
                if (moduleReference.ResolveToEntry(lod.FileRef) is ExportEntry module)
                {
                    yield return module;
                }
            }
        }
    }

    private static void RecordPropertyCoverage(ExportEntry export, VfxPreviewDefinition definition)
    {
        AppliedProperties.TryGetValue(export.ClassName, out HashSet<string> applied);
        foreach (Property property in export.GetProperties())
        {
            VfxPropertyCoverageStatus status = MetadataProperties.Contains(property.Name.Name)
                ? VfxPropertyCoverageStatus.Metadata
                : applied?.Contains(property.Name.Name) == true
                    ? VfxPropertyCoverageStatus.Applied
                    : VfxPropertyCoverageStatus.Unsupported;
            definition.PropertyCoverage.Add(new VfxPropertyCoverage(export.InstancedFullPath, export.ClassName, property.Name.Name, status));
            if (status == VfxPropertyCoverageStatus.Unsupported)
            {
                definition.Warnings.Add($"{export.ObjectName.Instanced}.{property.Name.Name} is serialized but not applied by the preview.");
            }
        }
    }

    private static VfxEmitterDefinition ParseEmitter(ExportEntry emitter, ExportEntry lod, List<string> warnings)
    {
        ExportEntry required = lod.GetProperty<ObjectProperty>("RequiredModule")?.ResolveToEntry(lod.FileRef) as ExportEntry;
        ExportEntry spawn = lod.GetProperty<ObjectProperty>("SpawnModule")?.ResolveToEntry(lod.FileRef) as ExportEntry;
        ExportEntry typeData = lod.GetProperty<ObjectProperty>("TypeDataModule")?.ResolveToEntry(lod.FileRef) as ExportEntry;
        var modules = lod.GetProperty<ArrayProperty<ObjectProperty>>("Modules")?
            .Select(reference => reference.ResolveToEntry(lod.FileRef))
            .OfType<ExportEntry>()
            .Where(module => module.GetProperty<BoolProperty>("bEnabled")?.Value != false)
            .ToList() ?? [];

        IVfxDistribution<float> lifetime = new VfxConstantDistribution<float>(1);
        IVfxDistribution<Vector3> location = new VfxConstantDistribution<Vector3>(Vector3.Zero);
        IVfxDistribution<Vector3> velocity = new VfxConstantDistribution<Vector3>(Vector3.Zero);
        IVfxDistribution<Vector3> size = new VfxConstantDistribution<Vector3>(Vector3.One);
        IVfxDistribution<Vector3> sizeOverLife = new VfxConstantDistribution<Vector3>(Vector3.One);
        IVfxDistribution<Vector4> initialColor = new VfxConstantDistribution<Vector4>(Vector4.One);
        IVfxDistribution<Vector4> colorOverLife = new VfxConstantDistribution<Vector4>(Vector4.One);
        IVfxDistribution<Vector4> colorScaleOverLife = new VfxConstantDistribution<Vector4>(Vector4.One);
        bool colorScaleUsesEmitterTime = false;
        IVfxDistribution<Vector3> velocityOverLife = new VfxConstantDistribution<Vector3>(Vector3.One);
        bool velocityOverLifeIsAbsolute = false;
        IVfxDistribution<Vector3> accelerationOverLife = new VfxConstantDistribution<Vector3>(Vector3.Zero);
        IVfxDistribution<float> rotation = new VfxConstantDistribution<float>(0);
        IVfxDistribution<float> rotationRate = new VfxConstantDistribution<float>(0);
        IVfxDistribution<float> subImageIndex = new VfxConstantDistribution<float>(0);
        IVfxDistribution<float> subUVFrameRate = new VfxConstantDistribution<float>(0);
        int subUVStartingFrame = 0;
        bool subUVUseEmitterTime = false;
        VfxAxisLock axisLock = VfxAxisLock.None;
        var spawnInitializers = new List<VfxSpawnInitializer>();

        foreach (ExportEntry module in modules)
        {
            switch (module.ClassName)
            {
                case "ParticleModuleLifetime":
                    lifetime = ReadFloatDistribution(module, "Lifetime", 1);
                    break;
                case "ParticleModuleLocation":
                    location = ReadVectorDistribution(module, Vector3.Zero, "StartLocationRw", "StartLocation");
                    spawnInitializers.Add(new VfxLocationSpawnInitializer(location));
                    break;
                case "ParticleModuleLocationDirect":
                    location = ReadVectorDistribution(module, Vector3.Zero, "LocationRw", "Location");
                    break;
                case "ParticleModuleVelocity":
                    velocity = ReadVectorDistribution(module, Vector3.Zero, "StartVelocityRw", "StartVelocity");
                    spawnInitializers.Add(new VfxVelocitySpawnInitializer(velocity));
                    break;
                case "ParticleModuleLocationPrimitiveCylinder":
                    spawnInitializers.Add(ReadCylinderInitializer(module));
                    break;
                case "ParticleModuleSize":
                case "ParticleModuleInitialSize":
                    size = ReadVectorDistribution(module, Vector3.One, "StartSizeRw", "StartSize");
                    break;
                case "ParticleModuleSizeMultiplyLife":
                    sizeOverLife = ReadVectorDistribution(module, Vector3.Zero, "LifeMultiplierRw", "LifeMultiplier");
                    break;
                case "ParticleModuleColor":
                    initialColor = CombineColor(
                        ReadVectorDistribution(module, Vector3.Zero, "StartColorRw", "StartColor"),
                        ReadFloatDistribution(module, "StartAlpha", 1));
                    break;
                case "ParticleModuleColorOverLife":
                    colorOverLife = CombineColor(
                        ReadVectorDistribution(module, Vector3.Zero, "ColorOverLifeRw", "ColorOverLife"),
                        ReadFloatDistribution(module, "AlphaOverLife", 1));
                    break;
                case "ParticleModuleColorScaleOverLife":
                    colorScaleOverLife = CombineColor(
                        ReadVectorDistribution(module, Vector3.One, "ColorScaleOverLifeRw"),
                        ReadFloatDistribution(module, "AlphaScaleOverLife", 1));
                    colorScaleUsesEmitterTime = module.GetProperty<BoolProperty>("bEmitterTime")?.Value == true;
                    break;
                case "ParticleModuleVelocityOverLifetime":
                    velocityOverLife = ReadVectorDistribution(module, Vector3.One, "VelOverLifeRw");
                    velocityOverLifeIsAbsolute = module.GetProperty<BoolProperty>("Absolute")?.Value == true;
                    break;
                case "ParticleModuleAcceleration":
                    spawnInitializers.Add(new VfxAccelerationSpawnInitializer(
                        ReadVectorDistribution(module, Vector3.Zero, "AccelerationRw")));
                    break;
                case "ParticleModuleAccelerationOverLifetime":
                    accelerationOverLife = ReadVectorDistribution(module, Vector3.Zero, "AccelOverLifeRw");
                    break;
                case "ParticleModuleOrbit":
                    spawnInitializers.Add(ReadOrbitInitializer(module));
                    break;
                case "ParticleModuleRotation":
                    rotation = Scale(ReadFloatDistribution(module, "StartRotation", 0), MathF.Tau);
                    break;
                case "ParticleModuleRotationRate":
                    rotationRate = Scale(ReadFloatDistribution(module, "StartRotationRate", 0), MathF.Tau);
                    break;
                case "ParticleModuleOrientationAxisLock":
                    axisLock = ParseAxisLock(module.GetProperty<EnumProperty>("LockAxisFlags")?.Value);
                    break;
                case "ParticleModuleSubUV":
                    subImageIndex = ReadFloatDistribution(module, "SubImageIndex", 0);
                    break;
                case "ParticleModuleSubUVMovie":
                    subImageIndex = ReadFloatDistribution(module, "SubImageIndex", 0);
                    subUVFrameRate = ReadFloatDistribution(module, "FrameRate", 30);
                    subUVStartingFrame = module.GetProperty<IntProperty>("StartingFrame")?.Value ?? 1;
                    subUVUseEmitterTime = module.GetProperty<BoolProperty>("bUseEmitterTime")?.Value == true;
                    break;
                case "ParticleModuleUberLTISIVCL":
                case "ParticleModuleUberLTISIVCLIL":
                case "ParticleModuleUberLTISIVCLILIRSSBLIRR":
                    lifetime = ReadFloatDistribution(module, "Lifetime", 1);
                    size = ReadVectorDistribution(module, Vector3.One, "StartSize");
                    velocity = ReadVectorDistribution(module, Vector3.Zero, "StartVelocity");
                    colorOverLife = CombineColor(
                        ReadVectorDistribution(module, Vector3.One, "ColorOverLife"),
                        ReadFloatDistribution(module, "AlphaOverLife", 1));
                    if (module.ClassName is "ParticleModuleUberLTISIVCLIL" or "ParticleModuleUberLTISIVCLILIRSSBLIRR")
                    {
                        location = ReadVectorDistribution(module, Vector3.Zero, "StartLocation");
                    }
                    if (module.ClassName == "ParticleModuleUberLTISIVCLILIRSSBLIRR")
                    {
                        rotation = Scale(ReadFloatDistribution(module, "StartRotation", 0), MathF.Tau);
                        rotationRate = Scale(ReadFloatDistribution(module, "StartRotationRate", 0), MathF.Tau);
                        sizeOverLife = ReadVectorDistribution(module, Vector3.One, "SizeLifeMultiplier");
                    }
                    break;
                default:
                    if (!SupportedModules.Contains(module.ClassName))
                    {
                        warnings.Add($"{emitter.ObjectName.Instanced}: {module.ClassName} is not simulated.");
                    }
                    break;
            }
        }

        bool processSpawnRate = spawn?.GetProperty<BoolProperty>("bProcessSpawnRate")?.Value != false;
        bool processBurstList = spawn?.GetProperty<BoolProperty>("bProcessBurstList")?.Value != false;
        IVfxDistribution<float> spawnRate = spawn is null
            ? ReadFloatDistribution(required, "SpawnRate", 0)
            : processSpawnRate
                ? new VfxProductFloatDistribution(
                    ReadFloatDistribution(spawn, "Rate", 20),
                    ReadFloatDistribution(spawn, "RateScale", 1))
                : new VfxConstantDistribution<float>(0);
        IReadOnlyList<VfxBurst> bursts = spawn is null
            ? ReadBursts(required)
            : processBurstList ? ReadBursts(spawn) : [];

        var emitterDefinition = new VfxEmitterDefinition
        {
            Name = emitter.GetProperty<NameProperty>("EmitterName")?.Value.Instanced ?? emitter.ObjectName.Instanced,
            Delay = required?.GetProperty<FloatProperty>("EmitterDelay")?.Value ?? 0,
            Duration = required?.GetProperty<FloatProperty>("EmitterDuration")?.Value ?? 1,
            Loops = required?.GetProperty<IntProperty>("EmitterLoops")?.Value ?? 0,
            MaxParticles = Math.Max(lod.GetProperty<IntProperty>("PeakActiveParticles")?.Value ?? 0, 4096),
            ScreenAlignment = ParseScreenAlignment(required?.GetProperty<EnumProperty>("ScreenAlignment")?.Value),
            AxisLock = axisLock,
            SubImagesHorizontal = Math.Max(1, required?.GetProperty<IntProperty>("SubImages_Horizontal")?.Value ?? 1),
            SubImagesVertical = Math.Max(1, required?.GetProperty<IntProperty>("SubImages_Vertical")?.Value ?? 1),
            SubUVInterpolation = ParseSubUVInterpolation(required?.GetProperty<EnumProperty>("InterpolationMethod")?.Value),
            SubImageIndex = subImageIndex,
            SubUVFrameRate = subUVFrameRate,
            SubUVStartingFrame = subUVStartingFrame,
            SubUVUseEmitterTime = subUVUseEmitterTime,
            Material = required?.GetProperty<ObjectProperty>("Material")?.ResolveToEntry(lod.FileRef),
            SpawnRate = spawnRate,
            Bursts = bursts,
            Lifetime = lifetime,
            InitialLocation = location,
            InitialVelocity = velocity,
            InitialSize = size,
            InitialRotation = rotation,
            RotationRate = rotationRate,
            InitialColor = initialColor,
            SizeOverLife = sizeOverLife,
            ColorOverLife = colorOverLife,
            ColorScaleOverLife = colorScaleOverLife,
            ColorScaleUsesEmitterTime = colorScaleUsesEmitterTime,
            VelocityOverLife = velocityOverLife,
            VelocityOverLifeIsAbsolute = velocityOverLifeIsAbsolute,
            AccelerationOverLife = accelerationOverLife,
            UseLocalSpace = required?.GetProperty<BoolProperty>("bUseLocalSpace")?.Value == true
            ,IsSpriteEmitter = typeData is null
        };
        emitterDefinition.SpawnInitializers.AddRange(spawnInitializers);
        return emitterDefinition;
    }

    private static VfxOrbitSpawnInitializer ReadOrbitInitializer(ExportEntry module) => new(
        ReadVectorDistribution(module, Vector3.Zero, "OffsetAmountRw"),
        ReadVectorDistribution(module, Vector3.Zero, "RotationAmountRw"),
        ReadVectorDistribution(module, Vector3.Zero, "RotationRateAmountRw"),
        ReadOrbitUsesEmitterTime(module, "OffsetOptions"),
        ReadOrbitUsesEmitterTime(module, "RotationOptions"),
        ReadOrbitUsesEmitterTime(module, "RotationRateOptions"));

    private static bool ReadOrbitUsesEmitterTime(ExportEntry module, string propertyName) =>
        module.GetProperty<StructProperty>(propertyName)?.GetProp<BoolProperty>("bUseEmitterTime")?.Value == true;

    private static VfxCylinderSpawnInitializer ReadCylinderInitializer(ExportEntry module) => new(
        ReadVectorDistribution(module, Vector3.Zero, "StartLocationRw"),
        ReadFloatDistribution(module, "StartRadius", 50),
        ReadFloatDistribution(module, "StartHeight", 50),
        ReadFloatDistribution(module, "VelocityScale", 1),
        module.GetProperty<EnumProperty>("HeightAxis")?.Value.Name switch
        {
            "PMLPC_HEIGHTAXIS_X" => VfxCylinderHeightAxis.X,
            "PMLPC_HEIGHTAXIS_Y" => VfxCylinderHeightAxis.Y,
            _ => VfxCylinderHeightAxis.Z
        },
        module.GetProperty<BoolProperty>("SurfaceOnly")?.Value == true,
        module.GetProperty<BoolProperty>("Velocity")?.Value == true,
        module.GetProperty<BoolProperty>("RadialVelocity")?.Value != false,
        module.GetProperty<BoolProperty>("Positive_X")?.Value != false,
        module.GetProperty<BoolProperty>("Positive_Y")?.Value != false,
        module.GetProperty<BoolProperty>("Positive_Z")?.Value != false,
        module.GetProperty<BoolProperty>("Negative_X")?.Value != false,
        module.GetProperty<BoolProperty>("Negative_Y")?.Value != false,
        module.GetProperty<BoolProperty>("Negative_Z")?.Value != false);

    private static IReadOnlyList<VfxBurst> ReadBursts(ExportEntry spawn)
    {
        if (spawn?.GetProperty<ArrayProperty<StructProperty>>("BurstList") is not { } burstList)
        {
            return [];
        }

        return burstList.Select(burst => new VfxBurst(
            burst.GetProp<FloatProperty>("Time")?.Value ?? 0,
            burst.GetProp<IntProperty>("Count")?.Value ?? 0,
            burst.GetProp<IntProperty>("CountLow")?.Value ?? -1)).ToList();
    }

    internal static IVfxDistribution<float> ReadFloatDistribution(ExportEntry module, string propertyName, float fallback)
    {
        StructProperty raw = module?.GetProperty<StructProperty>(propertyName);
        ArrayPropertyBase lookup = GetArrayProperty(raw, "LookupTable");
        if (lookup is { Count: > 2 } && lookup.Properties.All(property => property is FloatProperty))
        {
            int operation = raw.GetProp<ByteProperty>("Op")?.Value ?? 1;
            int chunkSize = raw.GetProp<ByteProperty>("LookupTableChunkSize")?.Value ?? 1;
            float startTime = raw.GetProp<FloatProperty>("LookupTableStartTime")?.Value ?? 0;
            float timeScale = raw.GetProp<FloatProperty>("LookupTableTimeScale")?.Value ?? 0;
            return new VfxRawFloatDistribution(lookup.Properties.Skip(2).Cast<FloatProperty>().Select(value => value.Value).ToList(), operation, chunkSize, startTime, timeScale, fallback);
        }

        if (raw?.GetProp<ObjectProperty>("Distribution")?.ResolveToEntry(module.FileRef) is ExportEntry distribution)
        {
            return distribution.ClassName switch
            {
                "DistributionFloatConstant" => new VfxConstantDistribution<float>(distribution.GetProperty<FloatProperty>("Constant")?.Value ?? fallback),
                "DistributionFloatUniform" => new VfxUniformFloatDistribution(
                    distribution.GetProperty<FloatProperty>("Min")?.Value ?? fallback,
                    distribution.GetProperty<FloatProperty>("Max")?.Value ?? fallback),
                _ => new VfxConstantDistribution<float>(fallback)
            };
        }

        return new VfxConstantDistribution<float>(fallback);
    }

    internal static IVfxDistribution<Vector3> ReadVectorDistribution(ExportEntry module, Vector3 fallback, params string[] propertyNames)
    {
        StructProperty raw = null;
        foreach (string propertyName in propertyNames)
        {
            raw = module.GetProperty<StructProperty>(propertyName);
            if (raw is not null)
            {
                break;
            }
        }
        if (raw is null)
        {
            return new VfxConstantDistribution<Vector3>(fallback);
        }

        int operation = raw.GetProp<ByteProperty>("Op")?.Value ?? 1;
        int chunkSize = raw.GetProp<ByteProperty>("LookupTableChunkSize")?.Value ?? 1;
        float startTime = raw.GetProp<FloatProperty>("LookupTableStartTime")?.Value ?? 0;
        float timeScale = raw.GetProp<FloatProperty>("LookupTableTimeScale")?.Value ?? 0;
        ArrayPropertyBase lookup = GetArrayProperty(raw, "LookupTable");
        if (raw.StructType == "BioRawDistributionRwVector3"
            && lookup is { Count: > 0 }
            && lookup.Properties.All(property => property is StructProperty))
        {
            return new VfxRawVectorDistribution(
                lookup.Properties.Cast<StructProperty>().Select(value => ReadVector(value, fallback)).ToList(),
                operation, chunkSize, startTime, timeScale, fallback);
        }

        if (lookup is { Count: > 4 } && lookup.Properties.All(property => property is FloatProperty))
        {
            var values = new List<Vector3>();
            for (int index = 2; index + 2 < lookup.Count; index += 3)
            {
                values.Add(new Vector3(
                    ((FloatProperty)lookup[index]).Value,
                    ((FloatProperty)lookup[index + 1]).Value,
                    ((FloatProperty)lookup[index + 2]).Value));
            }
            return new VfxRawVectorDistribution(values, operation, Math.Max(1, chunkSize / 3), startTime, timeScale, fallback);
        }

        if (raw?.GetProp<ObjectProperty>("Distribution")?.ResolveToEntry(module.FileRef) is ExportEntry distribution)
        {
            return distribution.ClassName switch
            {
                "DistributionVectorConstant" => new VfxConstantDistribution<Vector3>(ReadVector(distribution.GetProperty<StructProperty>("Constant"), fallback)),
                "DistributionVectorUniform" => new VfxUniformVectorDistribution(
                    ReadVector(distribution.GetProperty<StructProperty>("Min"), fallback),
                    ReadVector(distribution.GetProperty<StructProperty>("Max"), fallback)),
                _ => new VfxConstantDistribution<Vector3>(fallback)
            };
        }

        return new VfxConstantDistribution<Vector3>(fallback);
    }

    private static ArrayPropertyBase GetArrayProperty(StructProperty property, NameReference name) =>
        property?.Properties.FirstOrDefault(candidate => candidate.Name == name) as ArrayPropertyBase;

    private static Vector3 ReadVector(StructProperty property, Vector3 fallback) => property is null
        ? fallback
        : new Vector3(property.GetProp<FloatProperty>("X")?.Value ?? fallback.X,
            property.GetProp<FloatProperty>("Y")?.Value ?? fallback.Y,
            property.GetProp<FloatProperty>("Z")?.Value ?? fallback.Z);

    private static IVfxDistribution<Vector4> CombineColor(IVfxDistribution<Vector3> color, IVfxDistribution<float> alpha)
        => new CombinedColorDistribution(color, alpha);

    private static IVfxDistribution<float> Scale(IVfxDistribution<float> source, float scale)
        => new ScaledFloatDistribution(source, scale);

    private static VfxScreenAlignment ParseScreenAlignment(string value) => value switch
    {
        "PSA_Square" => VfxScreenAlignment.Square,
        "PSA_Rectangle" => VfxScreenAlignment.Rectangle,
        "PSA_Velocity" => VfxScreenAlignment.Velocity,
        "PSA_TypeSpecific" => VfxScreenAlignment.TypeSpecific,
        _ => VfxScreenAlignment.CameraFacing
    };

    private static VfxAxisLock ParseAxisLock(string value) => value switch
    {
        "EPAL_X" => VfxAxisLock.PositiveX,
        "EPAL_NEGATIVE_X" => VfxAxisLock.NegativeX,
        "EPAL_Y" => VfxAxisLock.PositiveY,
        "EPAL_NEGATIVE_Y" => VfxAxisLock.NegativeY,
        "EPAL_Z" => VfxAxisLock.PositiveZ,
        "EPAL_NEGATIVE_Z" => VfxAxisLock.NegativeZ,
        "EPAL_ROTATE_X" => VfxAxisLock.RotateX,
        "EPAL_ROTATE_Y" => VfxAxisLock.RotateY,
        "EPAL_ROTATE_Z" => VfxAxisLock.RotateZ,
        _ => VfxAxisLock.None
    };

    private static VfxSubUVInterpolation ParseSubUVInterpolation(string value) => value switch
    {
        "PSUVIM_Linear" => VfxSubUVInterpolation.Linear,
        "PSUVIM_Linear_Blend" => VfxSubUVInterpolation.LinearBlend,
        "PSUVIM_Random" => VfxSubUVInterpolation.Random,
        "PSUVIM_Random_Blend" => VfxSubUVInterpolation.RandomBlend,
        _ => VfxSubUVInterpolation.None
    };

    private sealed class CombinedColorDistribution(IVfxDistribution<Vector3> color, IVfxDistribution<float> alpha) : IVfxDistribution<Vector4>
    {
        public Vector4 Evaluate(float time, float random) => new(color.Evaluate(time, random), alpha.Evaluate(time, random));
    }

    private sealed class ScaledFloatDistribution(IVfxDistribution<float> source, float scale) : IVfxDistribution<float>
    {
        public float Evaluate(float time, float random) => source.Evaluate(time, random) * scale;
    }
}

public sealed class WrappedVfxSourceAdapter : IVfxSourceAdapter
{
    private readonly ParticleSystemSourceAdapter particleSystemAdapter = new();

    public bool CanAdapt(ExportEntry export) => export?.ClassName is "RvrClientEffect" or "BioVFXTemplate";

    public VfxPreviewDefinition CreateDefinition(ExportEntry export)
    {
        var result = new VfxPreviewDefinition { Name = export.ObjectName.Instanced };
        foreach (ExportEntry particleSystem in FindParticleSystems(export))
        {
            VfxPreviewDefinition nested = particleSystemAdapter.CreateDefinition(particleSystem);
            result.Emitters.AddRange(nested.Emitters);
            result.Warnings.AddRange(nested.Warnings);
            result.PropertyCoverage.AddRange(nested.PropertyCoverage);
        }

        if (result.Emitters.Count == 0)
        {
            result.Warnings.Add($"{export.ClassName} does not expose a directly referenced ParticleSystem; proprietary wrapper modules are not yet simulated.");
        }
        return result;
    }

    private static IEnumerable<ExportEntry> FindParticleSystems(ExportEntry wrapper)
    {
        var visited = new HashSet<int>();
        var pending = new Queue<ExportEntry>();
        pending.Enqueue(wrapper);
        while (pending.Count > 0)
        {
            ExportEntry current = pending.Dequeue();
            if (!visited.Add(current.UIndex))
            {
                continue;
            }

            foreach (ObjectProperty reference in current.GetProperties().OfType<ObjectProperty>())
            {
                if (reference.ResolveToEntry(wrapper.FileRef) is not ExportEntry child)
                {
                    continue;
                }
                if (child.ClassName == "ParticleSystem")
                {
                    yield return child;
                }
                else if (pending.Count < 128)
                {
                    pending.Enqueue(child);
                }
            }

            foreach (ArrayProperty<ObjectProperty> array in current.GetProperties().OfType<ArrayProperty<ObjectProperty>>())
            {
                foreach (ObjectProperty reference in array)
                {
                    if (reference.ResolveToEntry(wrapper.FileRef) is ExportEntry child && child.ClassName == "ParticleSystem")
                    {
                        yield return child;
                    }
                    else if (reference.ResolveToEntry(wrapper.FileRef) is ExportEntry nested && pending.Count < 128)
                    {
                        pending.Enqueue(nested);
                    }
                }
            }
        }
    }
}
