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
    /// <summary>
    /// Property names that exist purely for cooking, editor presentation, or structural traversal.
    /// These are serialized but have no standalone visual meaning in the preview, so they must not be
    /// reported as unsupported. Every name here was verified against the class layouts in Engine.pcc.
    /// </summary>
    private static readonly HashSet<string> MetadataProperties = new(StringComparer.Ordinal)
    {
        // UParticleModule flags and structural references the preview walks rather than applies directly.
        "LODValidity", "bEnabled", "bEditable", "LODDuplicate", "bSpawnModule", "bUpdateModule", "bFinalUpdateModule",
        "bSpawnRateModule", "bCurvesAsColor", "b3DDrawMode", "bSupported3DDrawMode",
        "Level", "PeakActiveParticles", "Modules", "RequiredModule", "SpawnModule", "TypeDataModule", "LODLevels", "Emitters",
        // UParticleLODLevel bookkeeping caches rebuilt at runtime.
        "SpawningModules", "SpawnModules", "UpdateModules", "OrbitModules", "EventReceiverModules", "SpawnRateModules",
        "ModuleOffsetMap", "ModuleInstanceOffsetMap", "EventGenerator", "ModuleMapsInstanceSize", "ModuleMapsParticleSize",
        "ModuleMapsTypeDataOffset", "ModuleMapsTypeDataInstanceOffset", "ModuleMapsCreated", "ConvertedModules",
        // UParticleEmitter cook/runtime bookkeeping.
        "SubUVDataOffset", "InitialAllocationCount", "bIsSoloing", "bCookedOut", "ModuleInstanceOffset", "PeakActiveParticleCount",
        "bIsSoloEnabled",
        // UParticleSystem cook/runtime bookkeeping with no standalone visual effect.
        "LODDistanceCheckTime", "bRegenerateLODDuplicate", "bShouldResetPeakCounts", "bHasPhysics", "bBioDependsOnPhysics",
        "bSkipSpawnCountCheck", "SoloTracking", "CurveEdSetup", "PreviewComponent", "SecondsBeforeInactive",
        "LODDistanceMultiplayerBias", "UpdateTime_FPS",
        // Editor-only presentation state.
        "EditorLODSetting", "ThumbnailDistance", "ThumbnailWarmup", "ThumbnailImage", "ThumbnailImageOutOfDate", "ThumbnailAngle",
        "bUseRealtimeThumbnail", "EmitterEditorColor", "EmitterRenderMode", "ModuleEditorColor", "bCollapsed",
        "bSupportsRandomSeed", "bRequiresLoopingNotification", "bUpdateForGPUEmitter",
        // Deterministic-seed support: the preview drives its own reproducible RNG.
        "m_Seed", "m_bUseSeed", "m_bUpdateSeed",
        // Non-visual gameplay/physics/perf switches.
        "CastShadows", "DoCollisions", "bAllowMotionBlur", "DownsampleThresholdScreenFraction", "bUseLegacyEmitterTime",
        "bScaleUV", "bDirectUV", "EmitterNormalsMode", "NormalsSphereCenter", "NormalsCylinderDirection",
        "MacroUVPosition", "MacroUVRadius", "CustomOcclusionBounds"
    };

    /// <summary>
    /// Per-class property names the preview actually consumes. Keys are the exact class names found in
    /// Engine.pcc; inherited names are resolved through <see cref="AppliedPropertyInheritance"/>.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> AppliedProperties = new(StringComparer.Ordinal)
    {
        ["ParticleSystem"] = new(StringComparer.Ordinal)
        {
            "FixedRelativeBoundingBox", "bUseFixedRelativeBoundingBox", "LODDistances", "LODSettings", "UpdateTime_Delta",
            "BioLockLowestLODToHighest", "WarmupTime", "Delay", "DelayLow", "bUseDelayRange", "bOrientZAxisTowardCamera"
        },
        ["ParticleEmitter"] = new(StringComparer.Ordinal) { "EmitterName", "Material" },
        ["ParticleSpriteEmitter"] = new(StringComparer.Ordinal) { "Material" },
        ["ParticleModuleRequired"] = new(StringComparer.Ordinal)
        {
            "SpawnRate", "BurstList", "Material", "ScreenAlignment", "SortMode", "ParticleBurstMethod", "InterpolationMethod",
            "SubImages_Horizontal", "SubImages_Vertical", "RandomImageTime", "RandomImageChanges",
            "EmitterDuration", "EmitterDurationLow", "bEmitterDurationUseRange", "bDurationRecalcEachLoop", "EmitterLoops",
            "EmitterDelay", "EmitterDelayLow", "bEmitterDelayUseRange", "bDelayFirstLoopOnly",
            "bUseLocalSpace", "bKillOnDeactivate", "bKillOnCompleted", "MaxDrawCount", "bUseMaxDrawCount"
        },
        ["ParticleModuleSpawnBase"] = new(StringComparer.Ordinal) { "bProcessSpawnRate", "bProcessBurstList" },
        ["ParticleModuleSpawn"] = new(StringComparer.Ordinal) { "Rate", "RateScale", "BurstList", "ParticleBurstMethod" },
        ["ParticleModuleLifetime"] = new(StringComparer.Ordinal) { "Lifetime" },
        ["ParticleModuleLocation"] = new(StringComparer.Ordinal) { "StartLocationRw", "StartLocation" },
        ["ParticleModuleLocationDirect"] = new(StringComparer.Ordinal) { "LocationRw", "Location" },
        ["ParticleModuleLocationPrimitiveBase"] = new(StringComparer.Ordinal)
        {
            "StartLocationRw", "StartLocation", "VelocityScale", "SurfaceOnly", "Velocity",
            "Positive_X", "Positive_Y", "Positive_Z", "Negative_X", "Negative_Y", "Negative_Z"
        },
        ["ParticleModuleLocationPrimitiveCylinder"] = new(StringComparer.Ordinal) { "StartRadius", "StartHeight", "HeightAxis", "RadialVelocity" },
        ["ParticleModuleLocationPrimitiveSphere"] = new(StringComparer.Ordinal) { "StartRadius" },
        ["ParticleModuleVelocity"] = new(StringComparer.Ordinal) { "StartVelocityRw", "StartVelocity", "StartVelocityRadial" },
        ["ParticleModuleVelocityBase"] = new(StringComparer.Ordinal) { "bInWorldSpace" },
        ["ParticleModuleVelocityOverLifetime"] = new(StringComparer.Ordinal) { "VelOverLifeRw", "Absolute" },
        ["ParticleModuleSize"] = new(StringComparer.Ordinal) { "StartSizeRw", "StartSize" },
        ["ParticleModuleSizeMultiplyLife"] = new(StringComparer.Ordinal) { "LifeMultiplierRw", "LifeMultiplier", "MultiplyX", "MultiplyY", "MultiplyZ" },
        ["ParticleModuleSizeMultiplyVelocity"] = new(StringComparer.Ordinal) { "VelocityMultiplierRw", "MultiplyX", "MultiplyY", "MultiplyZ" },
        ["ParticleModuleSizeScale"] = new(StringComparer.Ordinal) { "SizeScaleRw", "EnableX", "EnableY", "EnableZ" },
        ["ParticleModuleSizeScaleByTime"] = new(StringComparer.Ordinal) { "SizeScaleByTimeRw", "bEnableX", "bEnableY", "bEnableZ" },
        ["ParticleModuleColor"] = new(StringComparer.Ordinal) { "StartColorRw", "StartColor", "StartAlpha", "bClampAlpha" },
        ["ParticleModuleColorOverLife"] = new(StringComparer.Ordinal) { "ColorOverLifeRw", "ColorOverLife", "AlphaOverLife", "bClampAlpha" },
        ["ParticleModuleColorScaleOverLife"] = new(StringComparer.Ordinal) { "ColorScaleOverLifeRw", "AlphaScaleOverLife", "bEmitterTime" },
        ["ParticleModuleAcceleration"] = new(StringComparer.Ordinal) { "AccelerationRw", "bApplyOwnerScale" },
        ["ParticleModuleAccelerationBase"] = new(StringComparer.Ordinal) { "bAlwaysInWorldSpace" },
        ["ParticleModuleAccelerationOverLifetime"] = new(StringComparer.Ordinal) { "AccelOverLifeRw" },
        ["ParticleModuleOrbitBase"] = new(StringComparer.Ordinal) { "bUseEmitterTime" },
        ["ParticleModuleOrbit"] = new(StringComparer.Ordinal)
        {
            "OffsetAmountRw", "RotationAmountRw", "RotationRateAmountRw", "OffsetOptions", "RotationOptions", "RotationRateOptions", "ChainMode"
        },
        ["ParticleModuleRotation"] = new(StringComparer.Ordinal) { "StartRotation" },
        ["ParticleModuleRotationOverLifetime"] = new(StringComparer.Ordinal) { "RotationOverLife", "Scale" },
        ["ParticleModuleRotationRate"] = new(StringComparer.Ordinal) { "StartRotationRate" },
        ["ParticleModuleRotationRateMultiplyLife"] = new(StringComparer.Ordinal) { "LifeMultiplier" },
        ["ParticleModuleOrientationAxisLock"] = new(StringComparer.Ordinal) { "LockAxisFlags" },
        ["ParticleModuleKillBox"] = new(StringComparer.Ordinal) { "LowerLeftCornerRw", "UpperRightCornerRw", "bAbsolute", "bKillInside" },
        ["ParticleModuleKillHeight"] = new(StringComparer.Ordinal) { "Height", "bAbsolute", "bFloor" },
        ["ParticleModuleSubUV"] = new(StringComparer.Ordinal) { "SubImageIndex" },
        ["ParticleModuleSubUVSelect"] = new(StringComparer.Ordinal) { "SubImageSelectRw" },
        ["ParticleModuleSubUVMovie"] = new(StringComparer.Ordinal) { "FrameRate", "StartingFrame", "bUseEmitterTime" },
        ["ParticleModuleTypeDataMesh"] = new(StringComparer.Ordinal)
        {
            "Mesh", "Pitch", "Roll", "Yaw", "bCameraFacing", "bOverrideMaterial", "MeshAlignment", "AxisLockOption", "CameraFacingOption"
        },
        ["ParticleModuleMeshMaterial"] = new(StringComparer.Ordinal) { "MeshMaterials" },
        ["ParticleModuleMeshRotation"] = new(StringComparer.Ordinal) { "StartRotationRw", "bInheritParent" },
        ["ParticleModuleMeshRotationRate"] = new(StringComparer.Ordinal) { "StartRotationRateRw" },
        ["ParticleModuleMeshRotationRateMultiplyLife"] = new(StringComparer.Ordinal) { "LifeMultiplierRw" },
        ["ParticleModuleUberBase"] = new(StringComparer.Ordinal) { "RequiredModules" },
        ["ParticleModuleUberLTISIVCL"] = new(StringComparer.Ordinal)
        {
            "Lifetime", "StartSize", "StartVelocity", "StartVelocityRadial", "ColorOverLife", "AlphaOverLife"
        },
        ["ParticleModuleUberLTISIVCLIL"] = new(StringComparer.Ordinal)
        {
            "Lifetime", "StartSize", "StartVelocity", "StartVelocityRadial", "ColorOverLife", "AlphaOverLife", "StartLocation"
        },
        ["ParticleModuleUberLTISIVCLILIRSSBLIRR"] = new(StringComparer.Ordinal)
        {
            "Lifetime", "StartSize", "StartVelocity", "StartVelocityRadial", "ColorOverLife", "AlphaOverLife", "StartLocation",
            "StartRotation", "StartRotationRate", "SizeLifeMultiplier", "SizeMultiplyX", "SizeMultiplyY", "SizeMultiplyZ"
        }
    };

    /// <summary>
    /// Superclass chains taken from Engine.pcc so inherited properties resolve to the base class that declares them.
    /// </summary>
    private static readonly Dictionary<string, string> AppliedPropertyInheritance = new(StringComparer.Ordinal)
    {
        ["ParticleSpriteEmitter"] = "ParticleEmitter",
        ["ParticleModuleSpawn"] = "ParticleModuleSpawnBase",
        ["ParticleModuleSpawnPerUnit"] = "ParticleModuleSpawnBase",
        ["ParticleModuleLocationPrimitiveCylinder"] = "ParticleModuleLocationPrimitiveBase",
        ["ParticleModuleLocationPrimitiveSphere"] = "ParticleModuleLocationPrimitiveBase",
        ["ParticleModuleVelocity"] = "ParticleModuleVelocityBase",
        ["ParticleModuleVelocityOverLifetime"] = "ParticleModuleVelocityBase",
        ["ParticleModuleVelocityInheritParent"] = "ParticleModuleVelocityBase",
        ["ParticleModuleAcceleration"] = "ParticleModuleAccelerationBase",
        ["ParticleModuleAccelerationOverLifetime"] = "ParticleModuleAccelerationBase",
        ["ParticleModuleOrbit"] = "ParticleModuleOrbitBase",
        ["ParticleModuleSubUVMovie"] = "ParticleModuleSubUV",
        ["ParticleModuleUberLTISIVCL"] = "ParticleModuleUberBase",
        ["ParticleModuleUberLTISIVCLIL"] = "ParticleModuleUberBase",
        ["ParticleModuleUberLTISIVCLILIRSSBLIRR"] = "ParticleModuleUberBase"
    };

    private static bool IsApplied(string className, string propertyName)
    {
        string current = className;
        while (current is not null)
        {
            if (AppliedProperties.TryGetValue(current, out HashSet<string> applied) && applied.Contains(propertyName))
            {
                return true;
            }
            AppliedPropertyInheritance.TryGetValue(current, out current);
        }
        return false;
    }


    private static readonly HashSet<string> SupportedModules = new(StringComparer.Ordinal)
    {
        "ParticleModuleRequired",
        "ParticleModuleSpawn",
        "ParticleModuleLifetime",
        "ParticleModuleLocation",
        "ParticleModuleLocationDirect",
        "ParticleModuleLocationPrimitiveCylinder",
        "ParticleModuleLocationPrimitiveSphere",
        "ParticleModuleVelocity",
        "ParticleModuleSize",
        "ParticleModuleInitialSize",
        "ParticleModuleSizeMultiplyLife",
        "ParticleModuleSizeMultiplyVelocity",
        "ParticleModuleSizeScale",
        "ParticleModuleSizeScaleByTime",
        "ParticleModuleColor",
        "ParticleModuleColorOverLife",
        "ParticleModuleColorScaleOverLife",
        "ParticleModuleVelocityOverLifetime",
        "ParticleModuleAcceleration",
        "ParticleModuleAccelerationOverLifetime",
        "ParticleModuleOrbit",
        "ParticleModuleRotation",
        "ParticleModuleRotationOverLifetime",
        "ParticleModuleRotationRate",
        "ParticleModuleRotationRateMultiplyLife",
        "ParticleModuleKillBox",
        "ParticleModuleKillHeight",
        "ParticleModuleOrientationAxisLock",
        "ParticleModuleSubUV",
        "ParticleModuleSubUVSelect",
        "ParticleModuleSubUVMovie",
        "ParticleModuleUberLTISIVCL",
        "ParticleModuleUberLTISIVCLIL",
        "ParticleModuleUberLTISIVCLILIRSSBLIRR",
        "ParticleModuleTypeDataMesh",
        "ParticleModuleMeshMaterial",
        "ParticleModuleMeshRotation",
        "ParticleModuleMeshRotationRate",
        "ParticleModuleMeshRotationRateMultiplyLife"
    };

    public bool CanAdapt(ExportEntry export) => export?.ClassName == "ParticleSystem";

    public VfxPreviewDefinition CreateDefinition(ExportEntry export)
    {
        IReadOnlyList<float> lodDistances = ReadLodDistances(export);
        IReadOnlyList<VfxLodSetting> lodSettings = ReadLodSettings(export);
        bool lockLowestLodToHighest = export.GetProperty<BoolProperty>("BioLockLowestLODToHighest")?.Value == true;
        // BioLockLowestLODToHighest pins the system to the highest-detail LOD; otherwise the editor LOD selection wins.
        int selectedLodIndex = lockLowestLodToHighest
            ? 0
            : export.GetProperty<IntProperty>("EditorLODSetting") is { } editorLod
                ? Math.Max(0, editorLod.Value)
                : SelectPreviewLodIndex(lodDistances, lodSettings, null);
        VfxBounds? fixedLocalBounds = ReadFixedBounds(export);
        var definition = new VfxPreviewDefinition
        {
            Name = export.ObjectName.Instanced,
            SystemTransform = VfxPreviewDefinition.CreateUnitScaleCenteringTransform(fixedLocalBounds),
            LodDistances = lodDistances,
            LodSettings = lodSettings,
            SelectedLodIndex = selectedLodIndex,
            LockLowestLodToHighest = lockLowestLodToHighest,
            WarmupTime = Math.Max(0, export.GetProperty<FloatProperty>("WarmupTime")?.Value ?? 0),
            SystemDelay = export.GetProperty<FloatProperty>("Delay")?.Value ?? 0,
            SystemDelayLow = export.GetProperty<FloatProperty>("DelayLow")?.Value ?? 0,
            UseSystemDelayRange = export.GetProperty<BoolProperty>("bUseDelayRange")?.Value == true,
            OrientZAxisTowardCamera = export.GetProperty<BoolProperty>("bOrientZAxisTowardCamera")?.Value == true,
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
        foreach (Property property in export.GetProperties())
        {
            string propertyName = property.Name.Name;
            VfxPropertyCoverageStatus status = IsApplied(export.ClassName, propertyName)
                ? VfxPropertyCoverageStatus.Applied
                : MetadataProperties.Contains(propertyName)
                    ? VfxPropertyCoverageStatus.Metadata
                    : VfxPropertyCoverageStatus.Unsupported;
            definition.PropertyCoverage.Add(new VfxPropertyCoverage(export.InstancedFullPath, export.ClassName, propertyName, status));
            if (status == VfxPropertyCoverageStatus.Unsupported)
            {
                definition.Warnings.Add($"{export.ObjectName.Instanced}.{propertyName} is serialized but not applied by the preview.");
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
        IVfxDistribution<Vector3> meshStartRotation = new VfxConstantDistribution<Vector3>(Vector3.Zero);
        IVfxDistribution<Vector3> meshStartRotationRate = new VfxConstantDistribution<Vector3>(Vector3.Zero);
        IVfxDistribution<Vector3> meshRotationRateMultiplier = new VfxConstantDistribution<Vector3>(Vector3.One);
        IVfxDistribution<Vector3> sizeScale = new VfxConstantDistribution<Vector3>(Vector3.One);
        IVfxDistribution<Vector3> sizeScaleByTime = new VfxConstantDistribution<Vector3>(Vector3.One);
        IVfxDistribution<Vector3> sizeMultiplyVelocity = new VfxConstantDistribution<Vector3>(Vector3.One);
        IVfxDistribution<float> rotationOverLife = new VfxConstantDistribution<float>(0);
        bool rotationOverLifeScales = false;
        IVfxDistribution<float> rotationRateMultiplierOverLife = new VfxConstantDistribution<float>(1);
        IVfxDistribution<float> subImageSelect = null;
        var killVolumes = new List<VfxKillVolume>();
        IReadOnlyList<IEntry> meshSectionMaterials = [];
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
                    if (module.GetProperty<StructProperty>("StartVelocityRadial") is not null)
                    {
                        spawnInitializers.Add(new VfxRadialVelocitySpawnInitializer(
                            ReadFloatDistribution(module, "StartVelocityRadial", 0)));
                    }
                    break;
                case "ParticleModuleLocationPrimitiveCylinder":
                    spawnInitializers.Add(ReadCylinderInitializer(module));
                    break;
                case "ParticleModuleLocationPrimitiveSphere":
                    spawnInitializers.Add(ReadSphereInitializer(module));
                    break;
                case "ParticleModuleSize":
                case "ParticleModuleInitialSize":
                    size = ReadVectorDistribution(module, Vector3.One, "StartSizeRw", "StartSize");
                    break;
                case "ParticleModuleSizeMultiplyLife":
                    sizeOverLife = MaskSizeMultiplier(
                        ReadVectorDistribution(module, Vector3.Zero, "LifeMultiplierRw", "LifeMultiplier"),
                        module.GetProperty<BoolProperty>("MultiplyX")?.Value != false,
                        module.GetProperty<BoolProperty>("MultiplyY")?.Value != false,
                        module.GetProperty<BoolProperty>("MultiplyZ")?.Value != false);
                    break;
                case "ParticleModuleSizeScale":
                    sizeScale = MaskSizeMultiplier(
                        ReadVectorDistribution(module, Vector3.One, "SizeScaleRw"),
                        module.GetProperty<BoolProperty>("EnableX")?.Value != false,
                        module.GetProperty<BoolProperty>("EnableY")?.Value != false,
                        module.GetProperty<BoolProperty>("EnableZ")?.Value != false);
                    break;
                case "ParticleModuleSizeScaleByTime":
                    sizeScaleByTime = MaskSizeMultiplier(
                        ReadVectorDistribution(module, Vector3.One, "SizeScaleByTimeRw"),
                        module.GetProperty<BoolProperty>("bEnableX")?.Value != false,
                        module.GetProperty<BoolProperty>("bEnableY")?.Value != false,
                        module.GetProperty<BoolProperty>("bEnableZ")?.Value != false);
                    break;
                case "ParticleModuleSizeMultiplyVelocity":
                    sizeMultiplyVelocity = MaskSizeMultiplier(
                        ReadVectorDistribution(module, Vector3.One, "VelocityMultiplierRw"),
                        module.GetProperty<BoolProperty>("MultiplyX")?.Value != false,
                        module.GetProperty<BoolProperty>("MultiplyY")?.Value != false,
                        module.GetProperty<BoolProperty>("MultiplyZ")?.Value != false);
                    break;
                case "ParticleModuleColor":
                    initialColor = CombineColor(
                        ReadVectorDistribution(module, Vector3.Zero, "StartColorRw", "StartColor"),
                        ReadFloatDistribution(module, "StartAlpha", 1));
                    break;
                case "ParticleModuleColorOverLife":
                    colorOverLife = CombineColor(
                        ReadVectorDistribution(module, Vector3.Zero, "ColorOverLifeRw", "ColorOverLife"),
                        ReadFloatDistribution(module, "AlphaOverLife", 1),
                        module.GetProperty<BoolProperty>("bClampAlpha")?.Value != false);
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
                case "ParticleModuleRotationOverLifetime":
                    rotationOverLife = Scale(ReadFloatDistribution(module, "RotationOverLife", 0), MathF.Tau);
                    // ParticleModuleRotationOverLifetime.Scale selects between multiplying and adding the curve.
                    rotationOverLifeScales = module.GetProperty<BoolProperty>("Scale")?.Value != false;
                    break;
                case "ParticleModuleRotationRateMultiplyLife":
                    rotationRateMultiplierOverLife = ReadFloatDistribution(module, "LifeMultiplier", 1);
                    break;
                case "ParticleModuleKillBox":
                    killVolumes.Add(new VfxKillBox(
                        ReadVectorDistribution(module, Vector3.Zero, "LowerLeftCornerRw"),
                        ReadVectorDistribution(module, Vector3.Zero, "UpperRightCornerRw"),
                        module.GetProperty<BoolProperty>("bKillInside")?.Value == true,
                        module.GetProperty<BoolProperty>("bAbsolute")?.Value == true));
                    break;
                case "ParticleModuleKillHeight":
                    killVolumes.Add(new VfxKillHeight(
                        ReadFloatDistribution(module, "Height", 0),
                        module.GetProperty<BoolProperty>("bFloor")?.Value == true,
                        module.GetProperty<BoolProperty>("bAbsolute")?.Value == true));
                    break;
                case "ParticleModuleOrientationAxisLock":
                    axisLock = ParseAxisLock(module.GetProperty<EnumProperty>("LockAxisFlags")?.Value);
                    break;
                case "ParticleModuleMeshRotation":
                    // Mesh rotations are authored in units of full turns per axis, matching the sprite rotation modules.
                    meshStartRotation = Scale(ReadVectorDistribution(module, Vector3.Zero, "StartRotationRw"), MathF.Tau);
                    break;
                case "ParticleModuleMeshRotationRate":
                    meshStartRotationRate = Scale(ReadVectorDistribution(module, Vector3.Zero, "StartRotationRateRw"), MathF.Tau);
                    break;
                case "ParticleModuleMeshRotationRateMultiplyLife":
                    meshRotationRateMultiplier = ReadVectorDistribution(module, Vector3.One, "LifeMultiplierRw");
                    break;
                case "ParticleModuleMeshMaterial":
                    meshSectionMaterials = module.GetProperty<ArrayProperty<ObjectProperty>>("MeshMaterials")?
                        .Select(reference => reference.ResolveToEntry(module.FileRef))
                        .ToList() ?? [];
                    break;
                case "ParticleModuleSubUV":
                    subImageIndex = ReadFloatDistribution(module, "SubImageIndex", 0);
                    break;
                case "ParticleModuleSubUVSelect":
                    subImageSelect = ReadFloatDistribution(module, "SubImageSelectRw", 0);
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

        // ParticleModuleRequired.Material is the authoritative sprite material, but a lot of BioWare content
        // leaves it empty and relies on the legacy UParticleSpriteEmitter.Material instead.
        IEntry material = required?.GetProperty<ObjectProperty>("Material")?.ResolveToEntry(lod.FileRef)
            ?? emitter.GetProperty<ObjectProperty>("Material")?.ResolveToEntry(emitter.FileRef);
        VfxEmitterRenderMode renderMode = ClassifyRenderMode(typeData);
        VfxMeshEmitterDefinition meshEmitter = null;
        if (renderMode == VfxEmitterRenderMode.Mesh)
        {
            meshEmitter = new VfxMeshEmitterDefinition
            {
                Mesh = typeData.GetProperty<ObjectProperty>("Mesh")?.ResolveToEntry(typeData.FileRef),
                PreRotation = new Vector3(
                    typeData.GetProperty<FloatProperty>("Pitch")?.Value ?? 0,
                    typeData.GetProperty<FloatProperty>("Yaw")?.Value ?? 0,
                    typeData.GetProperty<FloatProperty>("Roll")?.Value ?? 0),
                CameraFacing = typeData.GetProperty<BoolProperty>("bCameraFacing")?.Value == true,
                OverrideMaterial = typeData.GetProperty<BoolProperty>("bOverrideMaterial")?.Value == true,
                MeshAlignment = ParseMeshAlignment(typeData.GetProperty<EnumProperty>("MeshAlignment")?.Value),
                CameraFacingOption = ParseMeshCameraFacing(typeData.GetProperty<EnumProperty>("CameraFacingOption")?.Value),
                AxisLockOption = ParseAxisLock(typeData.GetProperty<EnumProperty>("AxisLockOption")?.Value),
                SectionMaterialOverrides = meshSectionMaterials,
                StartRotation = meshStartRotation,
                StartRotationRate = meshStartRotationRate,
                RotationRateMultiplierOverLife = meshRotationRateMultiplier
            };
            if (meshEmitter.Mesh is null)
            {
                warnings.Add($"{emitter.ObjectName.Instanced}: the mesh type data module has no Mesh assigned, so nothing can be drawn.");
            }
        }
        else if (renderMode != VfxEmitterRenderMode.Sprite)
        {
            warnings.Add($"{emitter.ObjectName.Instanced}: {typeData.ClassName} emitters are not rendered by the preview yet.");
        }
        else if (material is null)
        {
            warnings.Add($"{emitter.ObjectName.Instanced}: no material is assigned on the required module or the emitter, so nothing can be drawn.");
        }

        var emitterDefinition = new VfxEmitterDefinition
        {
            Name = emitter.GetProperty<NameProperty>("EmitterName")?.Value.Instanced ?? emitter.ObjectName.Instanced,
            Delay = required?.GetProperty<FloatProperty>("EmitterDelay")?.Value ?? 0,
            DelayLow = required?.GetProperty<FloatProperty>("EmitterDelayLow")?.Value ?? 0,
            UseDelayRange = required?.GetProperty<BoolProperty>("bEmitterDelayUseRange")?.Value == true,
            DelayFirstLoopOnly = required?.GetProperty<BoolProperty>("bDelayFirstLoopOnly")?.Value == true,
            Duration = required?.GetProperty<FloatProperty>("EmitterDuration")?.Value ?? 1,
            DurationLow = required?.GetProperty<FloatProperty>("EmitterDurationLow")?.Value ?? 0,
            UseDurationRange = required?.GetProperty<BoolProperty>("bEmitterDurationUseRange")?.Value == true,
            RecalculateDurationEachLoop = required?.GetProperty<BoolProperty>("bDurationRecalcEachLoop")?.Value == true,
            Loops = required?.GetProperty<IntProperty>("EmitterLoops")?.Value ?? 0,
            KillOnDeactivate = required?.GetProperty<BoolProperty>("bKillOnDeactivate")?.Value == true,
            KillOnCompleted = required?.GetProperty<BoolProperty>("bKillOnCompleted")?.Value == true,
            SortMode = ParseSortMode(required?.GetProperty<EnumProperty>("SortMode")?.Value),
            BurstMethod = ParseBurstMethod((spawn ?? required)?.GetProperty<EnumProperty>("ParticleBurstMethod")?.Value),
            MaxDrawCount = required?.GetProperty<IntProperty>("MaxDrawCount")?.Value ?? 0,
            UseMaxDrawCount = required?.GetProperty<BoolProperty>("bUseMaxDrawCount")?.Value == true,
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
            RandomImageTime = Math.Max(0, required?.GetProperty<FloatProperty>("RandomImageTime")?.Value ?? 0),
            RandomImageChanges = Math.Max(0, required?.GetProperty<IntProperty>("RandomImageChanges")?.Value ?? 0),
            SubImageSelect = subImageSelect,
            Material = material,
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
            SizeScale = sizeScale,
            SizeScaleByTime = sizeScaleByTime,
            SizeMultiplyVelocity = sizeMultiplyVelocity,
            RotationOverLife = rotationOverLife,
            RotationOverLifeScales = rotationOverLifeScales,
            RotationRateMultiplierOverLife = rotationRateMultiplierOverLife,
            ColorOverLife = colorOverLife,
            ColorScaleOverLife = colorScaleOverLife,
            ColorScaleUsesEmitterTime = colorScaleUsesEmitterTime,
            VelocityOverLife = velocityOverLife,
            VelocityOverLifeIsAbsolute = velocityOverLifeIsAbsolute,
            AccelerationOverLife = accelerationOverLife,
            UseLocalSpace = required?.GetProperty<BoolProperty>("bUseLocalSpace")?.Value == true,
            KillVolumes = killVolumes,
            RenderMode = renderMode,
            MeshEmitter = meshEmitter
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

    private static VfxCylinderSpawnInitializer ReadCylinderInitializer(ExportEntry module) => new(        ReadVectorDistribution(module, Vector3.Zero, "StartLocationRw"),
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

    private static VfxSphereSpawnInitializer ReadSphereInitializer(ExportEntry module) => new(
        ReadVectorDistribution(module, Vector3.Zero, "StartLocationRw"),
        ReadFloatDistribution(module, "StartRadius", 50),
        ReadFloatDistribution(module, "VelocityScale", 1),
        module.GetProperty<BoolProperty>("SurfaceOnly")?.Value == true,
        module.GetProperty<BoolProperty>("Velocity")?.Value == true,
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
        => new CombinedColorDistribution(color, alpha, false);

    private static IVfxDistribution<Vector4> CombineColor(IVfxDistribution<Vector3> color, IVfxDistribution<float> alpha, bool clampAlpha)
        => new CombinedColorDistribution(color, alpha, clampAlpha);

    private static IVfxDistribution<Vector3> MaskSizeMultiplier(IVfxDistribution<Vector3> source, bool multiplyX, bool multiplyY, bool multiplyZ)
        => multiplyX && multiplyY && multiplyZ ? source : new MaskedVectorDistribution(source, multiplyX, multiplyY, multiplyZ);

    private static IVfxDistribution<float> Scale(IVfxDistribution<float> source, float scale)
        => new ScaledFloatDistribution(source, scale);

    private static IVfxDistribution<Vector3> Scale(IVfxDistribution<Vector3> source, float scale)
        => new ScaledVectorDistribution(source, scale);

    private static VfxEmitterRenderMode ClassifyRenderMode(ExportEntry typeData) => typeData?.ClassName switch
    {
        null or "ParticleModuleTypeDataSubUV" => VfxEmitterRenderMode.Sprite,
        "ParticleModuleTypeDataMesh" or "ParticleModuleTypeDataMeshPhysX" => VfxEmitterRenderMode.Mesh,
        "ParticleModuleTypeDataBeam" or "ParticleModuleTypeDataBeam2" => VfxEmitterRenderMode.Beam,
        "ParticleModuleTypeDataTrail" or "ParticleModuleTypeDataTrail2"
            or "ParticleModuleTypeDataAnimTrail" or "ParticleModuleTypeDataRibbon" => VfxEmitterRenderMode.Trail,
        _ => VfxEmitterRenderMode.Unsupported
    };

    private static VfxMeshAlignment ParseMeshAlignment(string value) => value switch
    {
        "PSMA_MeshFaceCameraWithRoll" => VfxMeshAlignment.FaceCameraWithRoll,
        "PSMA_MeshFaceCameraWithSpin" => VfxMeshAlignment.FaceCameraWithSpin,
        "PSMA_MeshFaceCameraWithLockedAxis" => VfxMeshAlignment.FaceCameraWithLockedAxis,
        _ => VfxMeshAlignment.FaceCameraWithRoll
    };

    private static VfxMeshCameraFacing ParseMeshCameraFacing(string value) => value switch
    {
        "XAxisFacing_ZUp" => VfxMeshCameraFacing.XAxisFacingZUp,
        "XAxisFacing_NegativeZUp" => VfxMeshCameraFacing.XAxisFacingNegativeZUp,
        "XAxisFacing_YUp" => VfxMeshCameraFacing.XAxisFacingYUp,
        "XAxisFacing_NegativeYUp" => VfxMeshCameraFacing.XAxisFacingNegativeYUp,
        "LockedAxis_ZAxisFacing" => VfxMeshCameraFacing.LockedAxisZAxisFacing,
        "LockedAxis_NegativeZAxisFacing" => VfxMeshCameraFacing.LockedAxisNegativeZAxisFacing,
        "LockedAxis_YAxisFacing" => VfxMeshCameraFacing.LockedAxisYAxisFacing,
        "LockedAxis_NegativeYAxisFacing" => VfxMeshCameraFacing.LockedAxisNegativeYAxisFacing,
        "VelocityAligned_ZAxisFacing" => VfxMeshCameraFacing.VelocityAlignedZAxisFacing,
        "VelocityAligned_NegativeZAxisFacing" => VfxMeshCameraFacing.VelocityAlignedNegativeZAxisFacing,
        "VelocityAligned_YAxisFacing" => VfxMeshCameraFacing.VelocityAlignedYAxisFacing,
        "VelocityAligned_NegativeYAxisFacing" => VfxMeshCameraFacing.VelocityAlignedNegativeYAxisFacing,
        _ => VfxMeshCameraFacing.XAxisFacingNoUp
    };

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

    private static VfxSortMode ParseSortMode(string value) => value switch
    {
        "PSORTMODE_ViewProjDepth" => VfxSortMode.ViewProjectionDepth,
        "PSORTMODE_DistanceToView" => VfxSortMode.DistanceToView,
        "PSORTMODE_Age_OldestFirst" => VfxSortMode.AgeOldestFirst,
        "PSORTMODE_Age_NewestFirst" => VfxSortMode.AgeNewestFirst,
        _ => VfxSortMode.None
    };

    private static VfxBurstMethod ParseBurstMethod(string value) => value switch
    {
        "EPBM_Interpolated" => VfxBurstMethod.Interpolated,
        _ => VfxBurstMethod.Instant
    };

    private sealed class CombinedColorDistribution(IVfxDistribution<Vector3> color, IVfxDistribution<float> alpha, bool clampAlpha) : IVfxDistribution<Vector4>
    {
        public Vector4 Evaluate(float time, float random)
        {
            float alphaValue = alpha.Evaluate(time, random);
            return new Vector4(color.Evaluate(time, random), clampAlpha ? Math.Clamp(alphaValue, 0, 1) : alphaValue);
        }
    }

    /// <summary>
    /// ParticleModuleSizeMultiplyLife only applies the life multiplier to the axes whose MultiplyX/Y/Z flag is set;
    /// the remaining axes keep their initial size.
    /// </summary>
    private sealed class MaskedVectorDistribution(IVfxDistribution<Vector3> source, bool useX, bool useY, bool useZ) : IVfxDistribution<Vector3>
    {
        public Vector3 Evaluate(float time, float random)
        {
            Vector3 value = source.Evaluate(time, random);
            return new Vector3(useX ? value.X : 1, useY ? value.Y : 1, useZ ? value.Z : 1);
        }
    }

    private sealed class ScaledFloatDistribution(IVfxDistribution<float> source, float scale) : IVfxDistribution<float>
    {
        public float Evaluate(float time, float random) => source.Evaluate(time, random) * scale;
    }

    private sealed class ScaledVectorDistribution(IVfxDistribution<Vector3> source, float scale) : IVfxDistribution<Vector3>
    {
        public Vector3 Evaluate(float time, float random) => source.Evaluate(time, random) * scale;
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
