using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
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

    public VfxPreviewDefinition CreateDefinition(ExportEntry export, PackageCache packageCache = null)
        => particleSystemAdapter.CanAdapt(export)
            ? particleSystemAdapter.CreateDefinition(export, packageCache)
            : wrappedAdapter.CreateDefinition(export, packageCache);
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
        ["ParticleModuleParameterDynamic"] = new(StringComparer.Ordinal) { "DynamicParams" },
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
    /// Visual Cascade fields handled by the preview's approximations. Several modules depend on a live level,
    /// physics scene, owning actor motion, or event graph. The preview still consumes their authored values and
    /// supplies deterministic standalone semantics instead of rejecting the complete emitter.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> StandaloneVisualProperties = new(StringComparer.Ordinal)
    {
        ["BioParticleModuleLocationAttachedMesh"] = new(StringComparer.Ordinal)
        {
            "m_SpecificationType", "m_EmissionAreaWeights", "m_EmissionAreaList", "bUseAttachedLocalSpace", "bUseRenderMeshAsSource"
        },
        ["ParticleModuleVelocityInheritParent"] = new(StringComparer.Ordinal) { "ScaleRw", "Scale" },
        ["ParticleModuleCollision"] = new(StringComparer.Ordinal)
        {
            "DampingFactorRotationRw", "DampingFactorRw", "DelayAmount", "MaxCollisions", "ParticleMass",
            "CollisionCompletionOption", "bOnlyVerticalNormalsDecrementCount", "bCollidePawns", "bDropDetail",
            "bPawnsDoNotDecrementCount", "VerticalFudgeFactor", "DirScalar", "DampingFactor", "DampingFactorRotation", "bApplyPhysics"
        },
        ["BioParticleModuleCollisionDecal"] = new(StringComparer.Ordinal)
        {
            "DampingFactorRotationRw", "DampingFactorRw", "DelayAmount", "MaxCollisions", "ParticleMass", "CollisionCompletionOption",
            "DecalTemplate", "bCollidePawns", "ColorParams", "bEnableMultiHitDecal", "bOnlyVerticalNormalsDecrementCount",
            "DampingFactor", "DampingFactorRotation", "bPawnsDoNotDecrementCount", "CollisionEmitter", "CollisionEmitterTemplate"
        },
        ["ParticleModuleColorByParameter"] = new(StringComparer.Ordinal) { "ColorParam", "DefaultColor" },
        ["ParticleModuleMaterialByParameter"] = new(StringComparer.Ordinal) { "MaterialParameters", "DefaultMaterials" },
        ["ParticleModuleLocationDirect"] = new(StringComparer.Ordinal)
        {
            "DirectionRw", "LocationOffsetRw", "ScaleFactorRw", "Direction", "LocationOffset", "ScaleFactor"
        },
        ["ParticleModuleLocationEmitter"] = new(StringComparer.Ordinal)
        {
            "EmitterName", "InheritSourceVelocity", "InheritSourceVelocityScale", "SelectionMethod", "bInheritSourceRotation"
        },
        ["ParticleModuleLocationEmitterDirect"] = new(StringComparer.Ordinal) { "EmitterName" },
        ["ParticleModuleSpawnPerUnit"] = new(StringComparer.Ordinal)
        {
            "SpawnPerUnit", "UnitScalar", "bIgnoreSpawnRateWhenMoving", "MovementTolerance"
        },
        ["ParticleModuleAttractorPoint"] = new(StringComparer.Ordinal)
        {
            "PositionRw", "Position", "Range", "Strength", "bAffectBaseVelocity", "bOverrideVelocity", "StrengthByDistance"
        },
        ["ParticleModuleAttractorParticle"] = new(StringComparer.Ordinal)
        {
            "EmitterName", "Range", "Strength", "LastSelIndex", "bInheritSourceVel", "bRenewSource", "bAffectBaseVelocity"
        },
        ["ParticleModuleTypeDataTrail2"] = new(StringComparer.Ordinal)
        {
            "MaxParticleInTrailCount", "TessellationStrength", "TextureTile", "TessellationFactor", "RenderGeometry",
            "bClipSourceSegement", "RenderDirectLine", "RenderTessellation", "RenderLines"
        },
        ["ParticleModuleTrailSource"] = new(StringComparer.Ordinal)
        {
            "SourceStrength", "SourceName", "SourceMethod", "bInheritRotation", "bLockSourceStength"
        },
        ["ParticleModuleTrailTaper"] = new(StringComparer.Ordinal) { "TaperFactor", "TaperMethod" },
        ["ParticleModuleTypeDataRibbon"] = new(StringComparer.Ordinal)
        {
            "bTangentRecalculationEveryFrame", "RenderAxis", "TilingDistance", "SheetsPerTrail"
        },
        ["ParticleModuleTypeDataAnimTrail"] = new(StringComparer.Ordinal) { "ControlEdgeName" },
        ["ParticleModuleBeamSource"] = new(StringComparer.Ordinal)
        {
            "SourceMethod", "SourceName", "SourceRw", "Source", "SourceStrength", "SourceTangentRw", "SourceTangent", "bSourceAbsolute",
            "bLockSource", "bLockSourceStength", "bLockSourceTangent"
        },
        ["ParticleModuleBeamTarget"] = new(StringComparer.Ordinal)
        {
            "LockRadius", "TargetMethod", "TargetName", "TargetRw", "Target", "TargetStrength", "TargetTangentRw", "TargetTangent", "bTargetAbsolute",
            "bLockTarget", "bLockTargetStength", "TargetTangentMethod"
        },
        ["ParticleModuleTypeDataBeam2"] = new(StringComparer.Ordinal)
        {
            "BranchParentName", "Distance", "MaxBeamCount", "Speed", "TaperFactor", "TaperScale", "TaperMethod", "Sheets",
            "TextureTile", "TextureTileDistance", "bAlwaysOn", "RenderTessellation", "InterpolationPoints"
        },
        ["ParticleModuleBeamNoise"] = new(StringComparer.Ordinal)
        {
            "bLowFreq_Enabled", "bSmooth", "Frequency", "Frequency_LowRange", "NoiseLockRadius", "NoiseRangeRw", "NoiseRangeScale",
            "NoiseScale", "NoiseSpeedRw", "NoiseTangentStrength", "NoiseTension", "NoiseTessellation", "bNRScaleEmitterTime",
            "bUseNoiseTangents", "bTargetNoise", "bOscillate", "FrequencyDistance", "NoiseRange", "NoiseSpeed",
            "NoiseLockTime", "bApplyNoiseScale"
        },
        ["ParticleModuleEventReceiverSpawn"] = new(StringComparer.Ordinal)
        {
            "EventGeneratorType", "EventName", "InheritVelocityScaleRw", "SpawnCount", "bUsePSysLocation"
        },
        ["ParticleModuleSourceMovement"] = new(StringComparer.Ordinal) { "SourceMovementScaleRw" },
        ["ParticleModuleEventGenerator"] = new(StringComparer.Ordinal) { "Events" },
        ["ParticleSystem"] = new(StringComparer.Ordinal)
        {
            "LODMethod", "OcclusionBoundsMethod", "bLit", "aFloatParameters", "aVectorParameters", "SystemUpdateMode", "fBioDebugValue"
        },
        ["ParticleSpriteEmitter"] = new(StringComparer.Ordinal)
        {
            "SpawnRate", "EmitterDuration", "EmitterLoops", "BurstList", "SubImages_Horizontal", "SubImages_Vertical",
            "InterpolationMethod", "UseLocalSpace", "ScaleUV", "KillOnDeactivate"
        },
        ["ParticleLODLevel"] = new(StringComparer.Ordinal) { "LevelSetting" },
        ["ParticleModuleAccelerationOverLifetime"] = new(StringComparer.Ordinal) { "AccelOverLife" },
        ["ParticleModuleVelocityOverLifetime"] = new(StringComparer.Ordinal) { "VelOverLife" },
        ["ParticleModuleAcceleration"] = new(StringComparer.Ordinal) { "Acceleration" },
        ["ParticleModuleColorScaleOverLife"] = new(StringComparer.Ordinal) { "ColorScaleOverLife" },
        ["ParticleModuleMeshRotation"] = new(StringComparer.Ordinal) { "StartRotation" },
        ["ParticleModuleMeshRotationRate"] = new(StringComparer.Ordinal) { "StartRotationRate" },
        ["ParticleModuleMeshRotationRateMultiplyLife"] = new(StringComparer.Ordinal) { "LifeMultiplier" },
        ["ParticleModuleSizeMultiplyVelocity"] = new(StringComparer.Ordinal) { "VelocityMultiplier" },
        ["ParticleModuleSizeScale"] = new(StringComparer.Ordinal) { "SizeScale" },
        ["ParticleModuleOrbit"] = new(StringComparer.Ordinal) { "OffsetAmount", "RotationAmount", "RotationRateAmount" },
        ["ParticleModuleTypeDataMesh"] = new(StringComparer.Ordinal)
        {
            "m_Meshes", "m_bMaterialCacheActive", "m_MaterialCache", "m_MaterialOverrides", "m_eSpawnOrder"
        },
        ["ParticleModuleTypeDataBeam"] = new(StringComparer.Ordinal)
        {
            "BeamMethod", "Distance", "EmitterStrength", "EndPoint", "EndPointDirection", "RenderDirectLine",
            "TargetStrength", "TessellationFactor", "TextureTile"
        },
        ["ParticleModuleRequired"] = new(StringComparer.Ordinal) { "bRequiresSorting" },
        ["BioParticleModuleMultiplyByEmitterSpeed"] = new(StringComparer.Ordinal) { "MaxUsedSpeed", "MultiplierAtMax" },
        ["BioParticleModuleVelocityWorldSpace"] = new(StringComparer.Ordinal) { "StartVelocityRw", "StartVelocityRadial" }
    };

    private static bool IsStandaloneVisualProperty(string className, string propertyName) =>
        StandaloneVisualProperties.TryGetValue(className, out HashSet<string> properties) && properties.Contains(propertyName);

    private static bool IsAudioModule(string className) => className is "BioParticleModuleSound" or "RvrCEffectModuleSound";

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
        "ParticleModuleParameterDynamic",
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
        "ParticleModuleMeshRotationRateMultiplyLife",
        // Standalone approximations for modules whose native behavior normally depends on an owning actor,
        // another emitter, the physics scene, or the Cascade event graph.
        "ParticleModuleVelocityInheritParent",
        "ParticleModuleCollision",
        "BioParticleModuleCollisionDecal",
        "ParticleModuleColorByParameter",
        "ParticleModuleMaterialByParameter",
        "ParticleModuleSpawnPerUnit",
        "ParticleModuleLocationEmitter",
        "ParticleModuleLocationEmitterDirect",
        "BioParticleModuleLocationAttachedMesh",
        "ParticleModuleAttractorPoint",
        "ParticleModuleAttractorParticle",
        "ParticleModuleTrailSource",
        "ParticleModuleTrailTaper",
        "ParticleModuleBeamSource",
        "ParticleModuleBeamTarget",
        "ParticleModuleBeamNoise",
        "ParticleModuleTypeDataBeam",
        "ParticleModuleEventReceiverSpawn",
        "ParticleModuleEventGenerator",
        "ParticleModuleSourceMovement",
        "BioParticleModuleMultiplyByEmitterSpeed",
        "BioParticleModuleVelocityWorldSpace",
        // Audio is deliberately ignored in this visual-only preview.
        "BioParticleModuleSound"
    };

    public bool CanAdapt(ExportEntry export) => export?.ClassName == "ParticleSystem";

    public VfxPreviewDefinition CreateDefinition(ExportEntry export, PackageCache packageCache = null)
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
            SystemTransform = VfxPreviewDefinition.CreateGridFittingTransform(fixedLocalBounds),
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
            // Old cooked packages can retain redirector/helper ParticleSystem exports with no serialized
            // emitter payload. They are valid database records but have nothing visual to simulate.
            return definition;
        }
        foreach (ObjectProperty emitterRef in emitterRefs)
        {
            if (emitterRef.ResolveToEntry(export.FileRef) is not ExportEntry emitter)
            {
                continue;
            }
            RecordPropertyCoverage(emitter, definition);

            ExportEntry lod = SelectEmitterLod(emitter, selectedLodIndex, packageCache);
            if (lod is null)
            {
                // Cooked seek-free packages retain empty emitter shells for stripped/scalability-only layers.
                // They contain no previewable payload and are intentionally skipped without an orange warning.
                continue;
            }

            RecordPropertyCoverage(lod, definition);
            foreach (ExportEntry child in EnumerateLodChildren(lod))
            {
                RecordPropertyCoverage(child, definition);
            }

            definition.Emitters.Add(ParseEmitter(emitter, lod, definition.Warnings, packageCache));
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

    public static ExportEntry SelectEmitterLod(ExportEntry emitter, int selectedLodIndex, PackageCache packageCache = null)
    {
        List<ExportEntry> lods = emitter.GetProperty<ArrayProperty<ObjectProperty>>("LODLevels")?
            .Select(reference => reference.ResolveToEntry(emitter.FileRef))
            .OfType<ExportEntry>()
            .ToList() ?? [];
        if (lods.Count == 0)
        {
            // Seek-free packages often serialize an empty emitter shell whose Cascade data lives on a shared
            // archetype. Follow that archetype just as the engine does.
            if (ResolveArchetypeExport(emitter, packageCache) is { } archetype
                && !ReferenceEquals(archetype, emitter))
            {
                return SelectEmitterLod(archetype, selectedLodIndex, packageCache);
            }
            return null;
        }

        int clampedIndex = Math.Clamp(selectedLodIndex, 0, lods.Count - 1);
        ExportEntry selected = lods[clampedIndex];
        if (selected.GetProperty<BoolProperty>("bEnabled")?.Value != false)
        {
            return selected;
        }

        // Individual emitters can disable the system-selected LOD while keeping another child enabled. For an
        // asset preview, rejecting the whole emitter hides useful layers; use the closest enabled child instead,
        // breaking a tie toward the higher-detail (lower numbered) LOD.
        return lods
            .Select((lod, index) => (Lod: lod, Index: index))
            .Where(candidate => candidate.Lod.GetProperty<BoolProperty>("bEnabled")?.Value != false)
            .OrderBy(candidate => Math.Abs(candidate.Index - clampedIndex))
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Lod)
            .FirstOrDefault()
            // Runtime scalability can enable a cooked LOD even when every child is serialized disabled.
            // In a standalone preview, the nearest authored child is preferable to dropping the emitter.
            ?? selected;
    }

    private static ExportEntry ResolveArchetypeExport(ExportEntry export, PackageCache packageCache) => export?.Archetype switch
    {
        ExportEntry archetype => archetype,
        ImportEntry import when packageCache is not null => EntryImporter.ResolveImport(import, packageCache),
        _ => null
    };

    private static IEntry ReadInheritedEntryProperty(ExportEntry export, string propertyName, PackageCache packageCache)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (export is not null && visited.Add($"{export.FileRef.FilePath}|{export.UIndex}"))
        {
            if (export.GetProperty<ObjectProperty>(propertyName)?.ResolveToEntry(export.FileRef) is { } value)
            {
                return value;
            }
            if (export.GetProperty<ArrayProperty<ObjectProperty>>(propertyName)?
                    .Select(reference => reference.ResolveToEntry(export.FileRef))
                    .FirstOrDefault(entry => entry is not null) is { } arrayValue)
            {
                return arrayValue;
            }
            export = ResolveArchetypeExport(export, packageCache);
        }
        return null;
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
                || IsStandaloneVisualProperty(export.ClassName, propertyName)
                ? VfxPropertyCoverageStatus.Applied
                : MetadataProperties.Contains(propertyName) || IsAudioModule(export.ClassName)
                    ? VfxPropertyCoverageStatus.Metadata
                    : VfxPropertyCoverageStatus.Unsupported;
            definition.PropertyCoverage.Add(new VfxPropertyCoverage(export.InstancedFullPath, export.ClassName, propertyName, status));
            if (status == VfxPropertyCoverageStatus.Unsupported)
            {
                definition.Warnings.Add($"{export.ObjectName.Instanced}.{propertyName} is serialized but not applied by the preview.");
            }
        }
    }

    private static VfxEmitterDefinition ParseEmitter(ExportEntry emitter, ExportEntry lod, List<string> warnings, PackageCache packageCache)
    {
        ExportEntry required = lod.GetProperty<ObjectProperty>("RequiredModule")?.ResolveToEntry(lod.FileRef) as ExportEntry
            ?? emitter;
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
        var dynamicParameters = new List<VfxDynamicParameterDefinition>();
        IEntry parameterMaterial = null;
        string attachedBone = null;
        VfxCollisionDefinition collision = null;
        var pointAttractors = new List<VfxPointAttractorDefinition>();
        var particleAttractors = new List<VfxParticleAttractorDefinition>();
        IVfxDistribution<Vector3> beamSource = new VfxConstantDistribution<Vector3>(Vector3.Zero);
        IVfxDistribution<Vector3> beamTarget = new VfxConstantDistribution<Vector3>(new Vector3(0, 0, 100));
        bool hasBeamSource = false;
        bool hasBeamTarget = false;
        bool eventDrivenSpawn = false;
        bool eventSpawnAtSystemLocation = false;
        IVfxDistribution<Vector3> sourceMovementScale = new VfxConstantDistribution<Vector3>(Vector3.Zero);

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
                    location = Add(
                        ReadVectorDistribution(module, Vector3.Zero, "LocationRw", "Location"),
                        ReadVectorDistribution(module, Vector3.Zero, "LocationOffsetRw", "LocationOffset"));
                    spawnInitializers.Add(new VfxVelocitySpawnInitializer(Multiply(
                        ReadVectorDistribution(module, Vector3.Zero, "DirectionRw", "Direction"),
                        ReadFloatDistribution(module, 1, "ScaleFactorRw", "ScaleFactor"))));
                    break;
                case "ParticleModuleLocationEmitter":
                    spawnInitializers.Add(new VfxEmitterLocationSpawnInitializer(
                        module.GetProperty<NameProperty>("EmitterName")?.Value.Instanced,
                        module.GetProperty<BoolProperty>("InheritSourceVelocity")?.Value == true,
                        module.GetProperty<FloatProperty>("InheritSourceVelocityScale")?.Value ?? 1));
                    break;
                case "ParticleModuleLocationEmitterDirect":
                    spawnInitializers.Add(new VfxEmitterLocationSpawnInitializer(
                        module.GetProperty<NameProperty>("EmitterName")?.Value.Instanced, true, 1));
                    break;
                case "BioParticleModuleLocationAttachedMesh":
                    attachedBone ??= ReadFirstAttachedBone(module);
                    break;
                case "ParticleModuleVelocity":
                case "BioParticleModuleVelocityWorldSpace":
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
                        ReadVectorDistribution(module, Vector3.One, "SizeScaleRw", "SizeScale"),
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
                        ReadVectorDistribution(module, Vector3.One, "VelocityMultiplierRw", "VelocityMultiplier"),
                        module.GetProperty<BoolProperty>("MultiplyX")?.Value != false,
                        module.GetProperty<BoolProperty>("MultiplyY")?.Value != false,
                        module.GetProperty<BoolProperty>("MultiplyZ")?.Value != false);
                    break;
                case "ParticleModuleColor":
                    // A color distribution the preview cannot read must fall back to white: black would silently
                    // remove the emitter from additive effects such as flame cards.
                    initialColor = CombineColor(
                        ReadVectorDistribution(module, Vector3.One, "StartColorRw", "StartColor"),
                        ReadFloatDistribution(module, "StartAlpha", 1));
                    break;
                case "ParticleModuleColorOverLife":
                    colorOverLife = CombineColor(
                        ReadVectorDistribution(module, Vector3.One, "ColorOverLifeRw", "ColorOverLife"),
                        ReadFloatDistribution(module, "AlphaOverLife", 1),
                        module.GetProperty<BoolProperty>("bClampAlpha")?.Value != false);
                    break;
                case "ParticleModuleColorScaleOverLife":
                    colorScaleOverLife = CombineColor(
                        ReadVectorDistribution(module, Vector3.One, "ColorScaleOverLifeRw", "ColorScaleOverLife"),
                        ReadFloatDistribution(module, "AlphaScaleOverLife", 1));
                    colorScaleUsesEmitterTime = module.GetProperty<BoolProperty>("bEmitterTime")?.Value == true;
                    break;
                case "ParticleModuleColorByParameter":
                    initialColor = new VfxConstantDistribution<Vector4>(ReadLinearColor(
                        module.GetProperty<StructProperty>("DefaultColor"), Vector4.One));
                    break;
                case "ParticleModuleMaterialByParameter":
                    parameterMaterial = module.GetProperty<ArrayProperty<ObjectProperty>>("DefaultMaterials")?
                        .Select(reference => reference.ResolveToEntry(module.FileRef))
                        .FirstOrDefault(entry => entry is not null);
                    break;
                case "ParticleModuleParameterDynamic":
                    dynamicParameters = ReadDynamicParameters(module);
                    break;
                case "ParticleModuleVelocityOverLifetime":
                    velocityOverLife = ReadVectorDistribution(module, Vector3.One, "VelOverLifeRw", "VelOverLife");
                    velocityOverLifeIsAbsolute = module.GetProperty<BoolProperty>("Absolute")?.Value == true;
                    break;
                case "ParticleModuleAcceleration":
                    spawnInitializers.Add(new VfxAccelerationSpawnInitializer(
                        ReadVectorDistribution(module, Vector3.Zero, "AccelerationRw", "Acceleration")));
                    break;
                case "ParticleModuleAccelerationOverLifetime":
                    accelerationOverLife = ReadVectorDistribution(module, Vector3.Zero, "AccelOverLifeRw", "AccelOverLife");
                    break;
                case "ParticleModuleAttractorPoint":
                    pointAttractors.Add(new VfxPointAttractorDefinition(
                        ReadVectorDistribution(module, Vector3.Zero, "PositionRw", "Position"),
                        ReadFloatDistribution(module, "Range", float.MaxValue),
                        ReadFloatDistribution(module, "Strength", 0),
                        module.GetProperty<BoolProperty>("StrengthByDistance")?.Value == true,
                        module.GetProperty<BoolProperty>("bOverrideVelocity")?.Value == true));
                    break;
                case "ParticleModuleAttractorParticle":
                    particleAttractors.Add(new VfxParticleAttractorDefinition(
                        module.GetProperty<NameProperty>("EmitterName")?.Value.Instanced,
                        ReadFloatDistribution(module, "Range", float.MaxValue),
                        ReadFloatDistribution(module, "Strength", 0),
                        module.GetProperty<BoolProperty>("bInheritSourceVel")?.Value == true));
                    break;
                case "ParticleModuleCollision":
                case "BioParticleModuleCollisionDecal":
                    collision = ReadCollision(module);
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
                    meshStartRotation = Scale(ReadVectorDistribution(module, Vector3.Zero, "StartRotationRw", "StartRotation"), MathF.Tau);
                    break;
                case "ParticleModuleMeshRotationRate":
                    meshStartRotationRate = Scale(ReadVectorDistribution(module, Vector3.Zero, "StartRotationRateRw", "StartRotationRate"), MathF.Tau);
                    break;
                case "ParticleModuleMeshRotationRateMultiplyLife":
                    meshRotationRateMultiplier = ReadVectorDistribution(module, Vector3.One, "LifeMultiplierRw", "LifeMultiplier");
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
                case "ParticleModuleBeamSource":
                    beamSource = ReadVectorDistribution(module, Vector3.Zero, "SourceRw", "Source");
                    hasBeamSource = true;
                    break;
                case "ParticleModuleBeamTarget":
                    beamTarget = ReadVectorDistribution(module, new Vector3(0, 0, 100), "TargetRw", "Target");
                    hasBeamTarget = true;
                    break;
                case "ParticleModuleEventReceiverSpawn":
                    // A standalone preview has no gameplay event graph. Keep event-only emitters visible by
                    // producing a small deterministic representative stream.
                    eventDrivenSpawn = true;
                    eventSpawnAtSystemLocation = module.GetProperty<BoolProperty>("bUsePSysLocation")?.Value == true;
                    break;
                case "ParticleModuleSourceMovement":
                    sourceMovementScale = ReadVectorDistribution(module, Vector3.Zero, "SourceMovementScaleRw");
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
        IVfxDistribution<float> spawnRate = spawn?.ClassName == "ParticleModuleSpawnPerUnit"
            // The owning component is stationary in the preview, so use the authored per-unit amount as a
            // representative time rate. Otherwise movement-only effects would remain permanently empty.
            ? new VfxProductFloatDistribution(
                ReadFloatDistribution(spawn, "SpawnPerUnit", 1),
                new VfxConstantDistribution<float>(Math.Max(1, spawn.GetProperty<FloatProperty>("UnitScalar")?.Value ?? 1)))
            : spawn is null
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
        IEntry material = parameterMaterial
            ?? ReadInheritedEntryProperty(required, "Material", packageCache)
            ?? ReadInheritedEntryProperty(emitter, "Material", packageCache)
            ?? typeData?.GetProperty<ObjectProperty>("Material")?.ResolveToEntry(typeData.FileRef)
            ?? ReadInheritedEntryProperty(typeData, "m_MaterialOverrides", packageCache);
        VfxEmitterRenderMode renderMode = ClassifyRenderMode(typeData);
        if (eventDrivenSpawn)
        {
            spawnRate = new MinimumFloatDistribution(spawnRate, 5);
        }
        if (renderMode == VfxEmitterRenderMode.Beam)
        {
            spawnRate = new MinimumFloatDistribution(spawnRate, 1);
        }
        VfxMeshEmitterDefinition meshEmitter = null;
        if (renderMode == VfxEmitterRenderMode.Mesh)
        {
            meshEmitter = new VfxMeshEmitterDefinition
            {
                Mesh = ReadInheritedEntryProperty(typeData, "Mesh", packageCache)
                    ?? ReadInheritedEntryProperty(typeData, "m_Meshes", packageCache),
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
        }
        else if (renderMode == VfxEmitterRenderMode.Unsupported)
        {
            warnings.Add($"{emitter.ObjectName.Instanced}: {typeData.ClassName} has no standalone preview renderer.");
        }

        VfxBeamDefinition beam = renderMode == VfxEmitterRenderMode.Beam
            ? new VfxBeamDefinition(
                hasBeamSource ? beamSource : new VfxConstantDistribution<Vector3>(Vector3.Zero),
                hasBeamTarget ? beamTarget : new VfxConstantDistribution<Vector3>(new Vector3(0, 0,
                    Math.Max(1, typeData?.GetProperty<FloatProperty>("Distance")?.Value ?? 100))),
                ReadFloatDistribution(typeData, "Distance", 100),
                Math.Max(0, typeData?.GetProperty<IntProperty>("InterpolationPoints")?.Value ?? 0))
            : null;

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
            KillOnDeactivate = required?.GetProperty<BoolProperty>("bKillOnDeactivate")?.Value == true
                || required?.GetProperty<BoolProperty>("KillOnDeactivate")?.Value == true,
            KillOnCompleted = required?.GetProperty<BoolProperty>("bKillOnCompleted")?.Value == true,
            SortMode = ParseSortMode(required?.GetProperty<EnumProperty>("SortMode")?.Value),
            BurstMethod = ParseBurstMethod((spawn ?? required)?.GetProperty<EnumProperty>("ParticleBurstMethod")?.Value),
            MaxDrawCount = required?.GetProperty<IntProperty>("MaxDrawCount")?.Value ?? 0,
            UseMaxDrawCount = required?.GetProperty<BoolProperty>("bUseMaxDrawCount")?.Value == true,
            MaxParticles = Math.Max(lod.GetProperty<IntProperty>("PeakActiveParticles")?.Value ?? 0, 4096),
            ScreenAlignment = ParseScreenAlignment(required?.GetProperty<EnumProperty>("ScreenAlignment")?.Value),
            AxisLock = axisLock,
            NormalsMode = ParseNormalsMode(required?.GetProperty<EnumProperty>("EmitterNormalsMode")?.Value),
            NormalsSphereCenter = required?.GetProperty<StructProperty>("NormalsSphereCenter") is { } sphereCenter
                ? CommonStructs.GetVector3(sphereCenter)
                : Vector3.Zero,
            NormalsCylinderDirection = required?.GetProperty<StructProperty>("NormalsCylinderDirection") is { } cylinderDirection
                ? CommonStructs.GetVector3(cylinderDirection)
                : Vector3.UnitZ,
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
            DynamicParameters = dynamicParameters,
            VelocityOverLife = velocityOverLife,
            VelocityOverLifeIsAbsolute = velocityOverLifeIsAbsolute,
            AccelerationOverLife = accelerationOverLife,
            SourceMovementScale = sourceMovementScale,
            EventSpawnAtSystemLocation = eventSpawnAtSystemLocation,
            UseLocalSpace = required?.GetProperty<BoolProperty>("bUseLocalSpace")?.Value == true
                || required?.GetProperty<BoolProperty>("UseLocalSpace")?.Value == true,
            KillVolumes = killVolumes,
            RenderMode = renderMode,
            MeshEmitter = meshEmitter,
            Collision = collision,
            PointAttractors = pointAttractors,
            ParticleAttractors = particleAttractors,
            Beam = beam,
            AttachmentBone = attachedBone
        };
        emitterDefinition.SpawnInitializers.AddRange(spawnInitializers);
        return emitterDefinition;
    }

    private static VfxCollisionDefinition ReadCollision(ExportEntry module) => new(
        ReadVectorDistribution(module, new Vector3(0.5f), "DampingFactorRw", "DampingFactor"),
        ReadFloatDistribution(module, "DelayAmount", 0),
        ReadFloatDistribution(module, "MaxCollisions", 1),
        module.GetProperty<EnumProperty>("CollisionCompletionOption")?.Value.Name switch
        {
            "EPCC_Kill" => VfxCollisionCompletion.Kill,
            "EPCC_HaltCollisions" => VfxCollisionCompletion.HaltCollisions,
            "EPCC_FreezeTranslation" => VfxCollisionCompletion.FreezeTranslation,
            "EPCC_FreezeRotation" => VfxCollisionCompletion.FreezeRotation,
            "EPCC_FreezeMovement" => VfxCollisionCompletion.FreezeMovement,
            _ => VfxCollisionCompletion.Freeze
        });

    private static Vector4 ReadLinearColor(StructProperty property, Vector4 fallback) => property is null
        ? fallback
        : new Vector4(
            property.GetProp<FloatProperty>("R")?.Value ?? fallback.X,
            property.GetProp<FloatProperty>("G")?.Value ?? fallback.Y,
            property.GetProp<FloatProperty>("B")?.Value ?? fallback.Z,
            property.GetProp<FloatProperty>("A")?.Value ?? fallback.W);

    private static string ReadFirstAttachedBone(ExportEntry module)
    {
        Property areaList = module.GetProperties().FirstOrDefault(property => property.Name == "m_EmissionAreaList");
        return EnumerateNames(areaList, module.FileRef, 0)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)
                && !string.Equals(name, "None", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateNames(Property property, IMEPackage package, int depth)
    {
        if (property is null || depth > 3)
        {
            yield break;
        }
        if (property is NameProperty name)
        {
            yield return name.Value.Instanced;
            yield break;
        }
        if (property is StructProperty structure)
        {
            foreach (Property child in structure.Properties)
            foreach (string value in EnumerateNames(child, package, depth + 1))
            {
                yield return value;
            }
            yield break;
        }
        if (property is ArrayPropertyBase array)
        {
            foreach (Property child in array.Properties)
            foreach (string value in EnumerateNames(child, package, depth + 1))
            {
                yield return value;
            }
            yield break;
        }
        if (property is ObjectProperty reference && reference.ResolveToEntry(package) is ExportEntry export)
        {
            foreach (Property child in export.GetProperties())
            foreach (string value in EnumerateNames(child, package, depth + 1))
            {
                yield return value;
            }
        }
    }

    private static List<VfxDynamicParameterDefinition> ReadDynamicParameters(ExportEntry module)
    {
        var result = new List<VfxDynamicParameterDefinition>(4);
        if (module.GetProperty<ArrayProperty<StructProperty>>("DynamicParams") is not { } parameters)
        {
            return result;
        }

        foreach (StructProperty parameter in parameters.Take(4))
        {
            result.Add(new VfxDynamicParameterDefinition(
                ReadFloatDistribution(parameter.GetProp<StructProperty>("ParamValue"), module, 1),
                ParseDynamicParameterValueMethod(parameter.GetProp<EnumProperty>("ValueMethod")?.Value.Name),
                parameter.GetProp<BoolProperty>("bUseEmitterTime")?.Value == true,
                parameter.GetProp<BoolProperty>("bSpawnTimeOnly")?.Value == true,
                parameter.GetProp<BoolProperty>("bScaleVelocityByParamValue")?.Value == true));
        }
        return result;
    }

    private static VfxDynamicParameterValueMethod ParseDynamicParameterValueMethod(string value) => value switch
    {
        "EDPV_VelocityX" => VfxDynamicParameterValueMethod.VelocityX,
        "EDPV_VelocityY" => VfxDynamicParameterValueMethod.VelocityY,
        "EDPV_VelocityZ" => VfxDynamicParameterValueMethod.VelocityZ,
        "EDPV_VelocityMag" => VfxDynamicParameterValueMethod.VelocityMagnitude,
        _ => VfxDynamicParameterValueMethod.UserSet
    };

    private static VfxOrbitSpawnInitializer ReadOrbitInitializer(ExportEntry module) => new(
        ReadVectorDistribution(module, Vector3.Zero, "OffsetAmountRw", "OffsetAmount"),
        ReadVectorDistribution(module, Vector3.Zero, "RotationAmountRw", "RotationAmount"),
        ReadVectorDistribution(module, Vector3.Zero, "RotationRateAmountRw", "RotationRateAmount"),
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
        return ReadFloatDistribution(raw, module, fallback);
    }

    private static IVfxDistribution<float> ReadFloatDistribution(ExportEntry module, float fallback, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (module?.GetProperty<StructProperty>(propertyName) is { } raw)
            {
                return ReadFloatDistribution(raw, module, fallback);
            }
        }
        return new VfxConstantDistribution<float>(fallback);
    }

    private static IVfxDistribution<float> ReadFloatDistribution(StructProperty raw, ExportEntry context, float fallback)
    {
        ArrayPropertyBase lookup = GetArrayProperty(raw, "LookupTable");
        if (lookup is { Count: > 2 } && lookup.Properties.All(property => property is FloatProperty))
        {
            int operation = raw.GetProp<ByteProperty>("Op")?.Value ?? 1;
            int chunkSize = raw.GetProp<ByteProperty>("LookupTableChunkSize")?.Value ?? 1;
            float startTime = raw.GetProp<FloatProperty>("LookupTableStartTime")?.Value ?? 0;
            float timeScale = raw.GetProp<FloatProperty>("LookupTableTimeScale")?.Value ?? 0;
            return new VfxRawFloatDistribution(lookup.Properties.Skip(2).Cast<FloatProperty>().Select(value => value.Value).ToList(), operation, chunkSize, startTime, timeScale, fallback);
        }

        if (raw?.GetProp<ObjectProperty>("Distribution")?.ResolveToEntry(context.FileRef) is ExportEntry distribution)
        {
            return distribution.ClassName switch
            {
                "DistributionFloatConstant" => new VfxConstantDistribution<float>(distribution.GetProperty<FloatProperty>("Constant")?.Value ?? fallback),
                "DistributionFloatUniform" => new VfxUniformFloatDistribution(
                    distribution.GetProperty<FloatProperty>("Min")?.Value ?? fallback,
                    distribution.GetProperty<FloatProperty>("Max")?.Value ?? fallback),
                "DistributionFloatParticleParameter" => new VfxConstantDistribution<float>(
                    distribution.GetProperty<FloatProperty>("Constant")?.Value ?? fallback),
                _ => new VfxConstantDistribution<float>(fallback)
            };
        }

        return new VfxConstantDistribution<float>(fallback);
    }

    public static IVfxDistribution<Vector3> ReadVectorDistribution(ExportEntry module, Vector3 fallback, params string[] propertyNames)
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
                "DistributionVectorParticleParameter" => new VfxConstantDistribution<Vector3>(
                    ReadVector(distribution.GetProperty<StructProperty>("Constant"), fallback)),
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

    private static IVfxDistribution<Vector3> Add(IVfxDistribution<Vector3> left, IVfxDistribution<Vector3> right)
        => new AddedVectorDistribution(left, right);

    private static IVfxDistribution<Vector3> Multiply(IVfxDistribution<Vector3> vector, IVfxDistribution<float> scalar)
        => new VectorTimesFloatDistribution(vector, scalar);

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
        "PSA_FacingCameraPosition" => VfxScreenAlignment.CameraFacing,
        // ScreenAlignment is omitted for the native enum default (PSA_Square) in a large amount of
        // cooked content. Treating an absent property as FacingCameraPosition collapses X-only fire
        // sprites to zero height.
        _ => VfxScreenAlignment.Square
    };

    private static VfxEmitterNormalsMode ParseNormalsMode(string value) => value switch
    {
        "ENM_Spherical" => VfxEmitterNormalsMode.Spherical,
        "ENM_Cylindrical" => VfxEmitterNormalsMode.Cylindrical,
        _ => VfxEmitterNormalsMode.CameraFacing
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

    private sealed class AddedVectorDistribution(IVfxDistribution<Vector3> left, IVfxDistribution<Vector3> right) : IVfxDistribution<Vector3>
    {
        public Vector3 Evaluate(float time, float random) => left.Evaluate(time, random) + right.Evaluate(time, random);
    }

    private sealed class VectorTimesFloatDistribution(IVfxDistribution<Vector3> vector, IVfxDistribution<float> scalar) : IVfxDistribution<Vector3>
    {
        public Vector3 Evaluate(float time, float random) => vector.Evaluate(time, random) * scalar.Evaluate(time, random);
    }

    private sealed class MinimumFloatDistribution(IVfxDistribution<float> source, float minimum) : IVfxDistribution<float>
    {
        public float Evaluate(float time, float random) => Math.Max(minimum, source.Evaluate(time, random));
    }
}

public sealed class WrappedVfxSourceAdapter : IVfxSourceAdapter
{
    private readonly ParticleSystemSourceAdapter particleSystemAdapter = new();

    public bool CanAdapt(ExportEntry export) => export?.ClassName is "RvrClientEffect" or "BioVFXTemplate";

    public VfxPreviewDefinition CreateDefinition(ExportEntry export, PackageCache packageCache = null)
    {
        var result = new VfxPreviewDefinition { Name = export.ObjectName.Instanced };
        VfxActorAttachment attachment = ReadActorAttachment(export);
        foreach (ExportEntry particleSystem in FindParticleSystems(export, packageCache))
        {
            VfxPreviewDefinition nested = particleSystemAdapter.CreateDefinition(particleSystem, packageCache);
            if (attachment is not null)
            {
                foreach (VfxEmitterDefinition emitter in nested.Emitters)
                {
                    emitter.AttachmentBone = attachment.BoneName;
                    emitter.AttachmentTransform = attachment.LocalTransform;
                }
            }
            result.Emitters.AddRange(nested.Emitters);
            result.Warnings.AddRange(nested.Warnings);
            result.PropertyCoverage.AddRange(nested.PropertyCoverage);
        }

        if (export.ClassName == "RvrClientEffect")
        {
            foreach (ExportEntry module in FindClientEffectModules(export, packageCache)
                         .Where(module => module.ClassName == "RvrCEffectModuleEffectsMaterial"
                             && module.GetProperty<BoolProperty>("m_bEnabled")?.Value != false))
            {
                string effectName = module.GetProperty<NameProperty>("m_nmEffect")?.Value.Instanced;
                if (string.IsNullOrWhiteSpace(effectName)
                    || string.Equals(effectName, "None", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string targetTag = module.GetProperty<NameProperty>("m_nmTag")?.Value.Instanced;
                var effect = new VfxActorMaterialEffectDefinition(effectName, targetTag);
                if (!result.ActorMaterialEffects.Contains(effect))
                {
                    result.ActorMaterialEffects.Add(effect);
                }
            }
        }

        // Legacy BioVFXTemplate payloads need simple standalone markers because they often contain no particle
        // system at all. RvrClientEffect modules are attached to the visible actor; drawing the same markers there
        // produces distracting wire lines over the character, so client effects show only real particle/mesh data.
        foreach (ExportEntry module in export.ClassName == "BioVFXTemplate"
                     ? EnumerateReferencedExports(export)
                     : Enumerable.Empty<ExportEntry>())
        {
            if (CreateStandaloneModulePreview(module, attachment) is { } standalone)
            {
                result.Emitters.Add(standalone);
            }
        }

        // Lifetime, parameter, location, and Sound/Wwise-only wrappers have no drawable payload. They are valid
        // effects, not a preview failure; the always-present actor still shows their attachment target.
        return result;
    }

    private static VfxEmitterDefinition CreateStandaloneModulePreview(ExportEntry module, VfxActorAttachment attachment)
    {
        if (module.GetProperty<BoolProperty>("m_bEnabled")?.Value == false)
        {
            return null;
        }
        VfxProceduralKind? kind = module.ClassName switch
        {
            "RvrCEffectModuleLensFlare" => VfxProceduralKind.LensFlare,
            "RvrCEffectModuleFramebuffer" => VfxProceduralKind.Framebuffer,
            "RvrCEffectModuleEffectsMaterial" => VfxProceduralKind.EffectsMaterial,
            "RvrCEffectModuleCameraShake" => VfxProceduralKind.CameraShake,
            "RvrCEffectModuleSkelMesh" => VfxProceduralKind.SkeletalMesh,
            "RvrCEffectModuleSpawnActor" => VfxProceduralKind.SpawnActor,
            // ME1/ME2 BioVFXTemplate exports reference these legacy payload assets directly.
            "Prefab" => VfxProceduralKind.SpawnActor,
            "BioCameraShake" => VfxProceduralKind.CameraShake,
            "PostProcessChain" => VfxProceduralKind.Framebuffer,
            "BioDecalComponent" => VfxProceduralKind.Decal,
            _ => null
        };
        if (kind is null)
        {
            return null;
        }

        float scale = Math.Max(0.1f, module.GetProperty<FloatProperty>("m_fDrawScale")?.Value ?? 1) * 50;
        Vector4 color = kind.Value switch
        {
            VfxProceduralKind.LensFlare => new Vector4(1, 0.85f, 0.25f, 1),
            VfxProceduralKind.Framebuffer => new Vector4(0.4f, 0.65f, 1, 0.8f),
            VfxProceduralKind.EffectsMaterial => new Vector4(0.25f, 1, 0.65f, 1),
            VfxProceduralKind.CameraShake => new Vector4(1, 0.35f, 0.25f, 1),
            VfxProceduralKind.Decal => new Vector4(1, 0.55f, 0.2f, 1),
            _ => new Vector4(0.75f, 0.75f, 1, 1)
        };
        IEntry sourceAsset = kind.Value switch
        {
            VfxProceduralKind.LensFlare => module.GetProperty<ObjectProperty>("m_pLensFlare")?.ResolveToEntry(module.FileRef),
            VfxProceduralKind.Framebuffer => module.GetProperty<ObjectProperty>("m_pPostProcess")?.ResolveToEntry(module.FileRef),
            VfxProceduralKind.SkeletalMesh or VfxProceduralKind.SpawnActor =>
                module.GetProperty<ObjectProperty>("m_pSkeletalMesh")?.ResolveToEntry(module.FileRef),
            VfxProceduralKind.Decal => module,
            _ when module.ClassName is "Prefab" or "PostProcessChain" or "BioCameraShake" => module,
            _ => null
        };
        return new VfxEmitterDefinition
        {
            Name = module.ObjectName.Instanced,
            Duration = Math.Max(1, module.GetProperty<FloatProperty>("m_fDuration")?.Value ?? 5),
            Lifetime = new VfxConstantDistribution<float>(5),
            RenderMode = VfxEmitterRenderMode.Procedural,
            Procedural = new VfxProceduralDefinition(kind.Value, scale, color, sourceAsset),
            AttachmentBone = attachment?.BoneName,
            AttachmentTransform = attachment?.LocalTransform ?? Matrix4x4.Identity
        };
    }

    private sealed record VfxActorAttachment(string BoneName, Matrix4x4 LocalTransform);

    private static VfxActorAttachment ReadActorAttachment(ExportEntry wrapper)
    {
        ExportEntry locationModule = EnumerateReferencedExports(wrapper)
            .FirstOrDefault(module => module.ClassName == "RvrCEffectModuleLocation");
        if (locationModule is null)
        {
            return null;
        }

        bool attachToBone = locationModule.GetProperty<BoolProperty>("m_bAttach")?.Value != false
            && string.Equals(locationModule.GetProperty<EnumProperty>("m_eReference")?.Value.Name,
                "ELR_Bone", StringComparison.Ordinal);
        string boneName = attachToBone
            ? locationModule.GetProperty<NameProperty>("m_sAttachment")?.Value.Instanced
            : null;
        if (string.Equals(boneName, "None", StringComparison.OrdinalIgnoreCase))
        {
            boneName = null;
        }

        Vector3 location = ParticleSystemSourceAdapter.ReadVectorDistribution(
            locationModule, Vector3.Zero, "m_LocationAdjust").Evaluate(0, 0.5f);
        Vector3 rotation = ParticleSystemSourceAdapter.ReadVectorDistribution(
            locationModule, Vector3.Zero, "m_RotationAdjust").Evaluate(0, 0.5f);
        Quaternion orientation = Quaternion.CreateFromYawPitchRoll(
            rotation.Z * MathF.Tau,
            rotation.Y * MathF.Tau,
            rotation.X * MathF.Tau);
        Matrix4x4 localTransform = Matrix4x4.CreateFromQuaternion(orientation)
            * Matrix4x4.CreateTranslation(location);
        return new VfxActorAttachment(boneName, localTransform);
    }

    private static IEnumerable<ExportEntry> EnumerateReferencedExports(ExportEntry wrapper)
    {
        foreach (ObjectProperty reference in wrapper.GetProperties().OfType<ObjectProperty>())
        {
            if (reference.ResolveToEntry(wrapper.FileRef) is ExportEntry export)
            {
                yield return export;
            }
        }
        foreach (ArrayProperty<ObjectProperty> array in wrapper.GetProperties().OfType<ArrayProperty<ObjectProperty>>())
        {
            foreach (ObjectProperty reference in array)
            {
                if (reference.ResolveToEntry(wrapper.FileRef) is ExportEntry export)
                {
                    yield return export;
                }
            }
        }
    }

    private static IEnumerable<ExportEntry> FindParticleSystems(ExportEntry wrapper, PackageCache packageCache)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<ExportEntry>();
        pending.Enqueue(wrapper);
        while (pending.Count > 0)
        {
            ExportEntry current = pending.Dequeue();
            if (!visited.Add($"{current.FileRef.FilePath}|{current.UIndex}"))
            {
                continue;
            }

            foreach (ObjectProperty reference in current.GetProperties().OfType<ObjectProperty>())
            {
                if (ResolveReferencedExport(reference, current.FileRef, packageCache) is not ExportEntry child)
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
                    ExportEntry resolved = ResolveReferencedExport(reference, current.FileRef, packageCache);
                    if (resolved is { ClassName: "ParticleSystem" } child)
                    {
                        yield return child;
                    }
                    else if (resolved is { } nested && pending.Count < 128)
                    {
                        pending.Enqueue(nested);
                    }
                }
            }
        }
    }

    private static IEnumerable<ExportEntry> FindClientEffectModules(ExportEntry wrapper, PackageCache packageCache)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<ExportEntry>();
        pending.Enqueue(wrapper);
        while (pending.Count > 0)
        {
            ExportEntry current = pending.Dequeue();
            if (!visited.Add($"{current.FileRef.FilePath}|{current.UIndex}"))
            {
                continue;
            }

            IEnumerable<ObjectProperty> references = current.GetProperties().OfType<ObjectProperty>()
                .Concat(current.GetProperties().OfType<ArrayProperty<ObjectProperty>>().SelectMany(array => array));
            foreach (ObjectProperty reference in references)
            {
                ExportEntry child = ResolveReferencedExport(reference, current.FileRef, packageCache);
                if (child is null)
                {
                    continue;
                }
                if (child.ClassName.StartsWith("RvrCEffectModule", StringComparison.Ordinal))
                {
                    yield return child;
                }
                else if (child.ClassName == "RvrClientEffect" && pending.Count < 128)
                {
                    pending.Enqueue(child);
                }
            }
        }
    }

    private static ExportEntry ResolveReferencedExport(ObjectProperty reference, IMEPackage package, PackageCache packageCache) =>
        reference.ResolveToEntry(package) switch
        {
            ExportEntry export => export,
            ImportEntry import when packageCache is not null => EntryImporter.ResolveImport(import, packageCache),
            _ => null
        };
}
