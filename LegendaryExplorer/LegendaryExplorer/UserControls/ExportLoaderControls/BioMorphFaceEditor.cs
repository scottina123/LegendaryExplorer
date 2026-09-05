using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;
using BinaryMorphFace = LegendaryExplorerCore.Unreal.BinaryConverters.BioMorphFace;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

/// <summary>
/// BioMorphFace editor for every Mass Effect game. It inherits the mesh viewport so the preview
/// uses the same game-specific shader path as the regular mesh renderer.
/// </summary>
public sealed class BioMorphFaceEditor : MeshRenderer
{
    public BioMorphFaceEditor() : base(true)
    {
    }

    public override bool CanParse(ExportEntry exportEntry) =>
        exportEntry is { ClassName: "BioMorphFace", IsDefaultObject: false }
        && exportEntry.Game.IsMEGame();

    public override void PopOut()
    {
        if (CurrentLoadedExport is null)
        {
            return;
        }

        var window = new ExportLoaderHostedWindow(new BioMorphFaceEditor
        {
            IsMorphEditorReadOnly = IsMorphEditorReadOnly
        }, CurrentLoadedExport)
        {
            Title = $"{(IsMorphEditorReadOnly ? "Morph Preview" : "Morph Editor")} - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath} - {CurrentLoadedExport.FileRef.FilePath}"
        };
        window.Show();
    }
}

public partial class MeshRenderer
{
    private sealed record MorphFeatureSnapshot(string Name, float Value);
    private sealed record MorphTargetSnapshot(MorphTarget.MorphLODModel[] Lods, MorphTarget.BoneOffset[] BoneOffsets);
    private sealed record MorphTexturePreviewResolution(
        ExportEntry TextureExport, PreviewTextureCache.TextureEntry CachedTexture, string DisplayPath);
    private readonly record struct MorphSkinInfluences(
        int Bone0, int Bone1, int Bone2, int Bone3,
        float Weight0, float Weight1, float Weight2, float Weight3);

    private ExportEntry MorphBaseHeadExport;
    private ExportEntry MorphHairMeshExport;
    private SkeletalMesh MorphPreviewSkeletalMesh;
    private SkeletalMesh MorphPreviewHairMesh;
    private MeshBone[] MorphBindSkeleton = [];
    private MeshBone[] MorphHairBindSkeleton = [];
    private MorphSkinInfluences[][] MorphSkinningInfluences = [];
    private MorphSkinInfluences[][] MorphHairSkinningInfluences = [];
    private Vector3[][] MorphHairBindLods = [];
    private ModelPreview<WorldVertex> MorphHairLEXPreview;
    private ModelPreview<LEVertex> MorphHairGameShaderPreview;
    private bool _hideMorphHair;
    private Vector3[][] StoredMorphLods = [];
    private Vector3[][] WorkingMorphLods = [];
    private Vector3[][] WorkingMorphNormalDeltas = [];
    private List<MorphFeatureSnapshot> OriginalMorphFeatures = [];
    private Dictionary<string, MorphTargetSnapshot> MorphTargets = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Vector3> BaseSkeletonPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> RemovedMorphBones = new(StringComparer.OrdinalIgnoreCase);
    private bool SuppressMorphEditorChanges;
    private string PendingMorphTargetStatus;
    private readonly Dictionary<int, MorphTexturePreviewResolution> MorphTexturePreviewCache = [];
    private DispatcherTimer MorphMaterialPreviewTimer;

    private string _morphSaveHelpText =
        "Override writes this export. Make new clones the morph and its material override, then writes the edited values to the clone.";
    public string MorphSaveHelpText
    {
        get => _morphSaveHelpText;
        set => SetProperty(ref _morphSaveHelpText, value);
    }

    private string _morphOverrideLabel = "Override morph";
    public string MorphOverrideLabel
    {
        get => _morphOverrideLabel;
        set => SetProperty(ref _morphOverrideLabel, value);
    }

    private string _morphSaveAsNewLabel = "Make new morph…";
    public string MorphSaveAsNewLabel
    {
        get => _morphSaveAsNewLabel;
        set => SetProperty(ref _morphSaveAsNewLabel, value);
    }

    /// <summary>
    /// Optional host callbacks used when Morph Editor is embedded in another export editor.
    /// The creator returns the export that receives the edited morph values; the completion
    /// callback can link that export to its host and return a contextual status message.
    /// </summary>
    public Func<string, (bool IsValid, string Error)> MorphNewNameValidatorOverride { get; set; }
    public Func<ExportEntry, string, ExportEntry> MorphSaveTargetCreatorOverride { get; set; }
    public Func<ExportEntry, string> MorphOverrideCompletedOverride { get; set; }
    public Func<ExportEntry, string> MorphSaveAsNewCompletedOverride { get; set; }

    private bool _allowMorphOverride = true;
    public bool AllowMorphOverride
    {
        get => _allowMorphOverride;
        set
        {
            if (SetProperty(ref _allowMorphOverride, value))
            {
                OnPropertyChanged(nameof(CanOverrideMorph));
            }
        }
    }
    public bool CanOverrideMorph => CanEditMorph && AllowMorphOverride;

    public ObservableCollectionExtended<MorphFeatureEditorItem> MorphFeatureItems { get; } = [];
    public IEnumerable<MorphFeatureEditorItem> MatchedMorphFeatureItems =>
        MorphFeatureItems.Where(feature => feature.HasMorphTarget && MatchesMorphViewportFeature(feature) && MatchesMorphEditorSearch(feature.Name)).ToArray();
    public IEnumerable<MorphFeatureEditorItem> UnmatchedMorphFeatureItems =>
        MorphFeatureItems.Where(feature => !feature.HasMorphTarget && MatchesMorphViewportFeature(feature) && MatchesMorphEditorSearch(feature.Name)).ToArray();
    public int UnmatchedMorphFeatureCount => UnmatchedMorphFeatureItems.Count();
    public bool HasUnmatchedMorphFeatures => UnmatchedMorphFeatureCount > 0;
    public ObservableCollectionExtended<MorphBoneEditorItem> MorphSkeletonItems { get; } = [];
    public IEnumerable<MorphBoneEditorItem> FilteredMorphSkeletonItems =>
        MorphSkeletonItems.Where(bone => MatchesMorphViewportBone(bone) && MatchesMorphEditorSearch(bone.Name)).ToArray();
    public ObservableCollectionExtended<MorphScalarOverrideItem> MorphScalarOverrides { get; } = [];
    public IEnumerable<MorphScalarOverrideItem> FilteredMorphScalarOverrides =>
        MorphScalarOverrides.Where(scalar => MatchesMorphViewportMaterial(scalar.Name, 0) && MatchesMorphEditorSearch(scalar.Name)).ToArray();
    public ObservableCollectionExtended<MorphColorOverrideItem> MorphColorOverrides { get; } = [];
    public IEnumerable<MorphColorOverrideItem> FilteredMorphColorOverrides =>
        MorphColorOverrides.Where(color => MatchesMorphViewportMaterial(color.Name, 1) && MatchesMorphEditorSearch(color.Name)).ToArray();
    public ObservableCollectionExtended<MorphTextureOverrideItem> MorphTextureOverrides { get; } = [];
    public IEnumerable<MorphTextureOverrideItem> FilteredMorphTextureOverrides => MorphTextureOverrides
        .Where(texture => MatchesMorphViewportMaterial(texture.Name, 2) && MatchesMorphEditorSearch(texture.Name, texture.ResolvedPath, texture.EntryIndex.ToString()))
        .ToArray();

    public bool HideMorphHair
    {
        get => _hideMorphHair;
        set
        {
            if (SetProperty(ref _hideMorphHair, value) && value && morphViewportHit?.Hair == true)
                ClearMorphViewportSelection();
        }
    }

    private string _morphEditorSearchText;
    public string MorphEditorSearchText
    {
        get => _morphEditorSearchText;
        set
        {
            if (SetProperty(ref _morphEditorSearchText, value))
            {
                RefreshMorphEditorFilters();
            }
        }
    }

    private float _morphRandomizationStrength = 1f;
    /// <summary>
    /// Scales the displacement from the current face to a generated BioWare face.
    /// One preserves the original randomizer behavior; zero leaves the current values
    /// unchanged, and values above one extrapolate past the generated reference.
    /// </summary>
    public float MorphRandomizationStrength
    {
        get => _morphRandomizationStrength;
        set
        {
            if (float.IsFinite(value))
            {
                SetProperty(ref _morphRandomizationStrength, Math.Clamp(value, 0f, 2f));
            }
        }
    }

    private string _morphBaseHeadPath;
    public string MorphBaseHeadPath
    {
        get => _morphBaseHeadPath;
        private set => SetProperty(ref _morphBaseHeadPath, value);
    }

    private string _morphHairMeshPath;
    public string MorphHairMeshPath
    {
        get => _morphHairMeshPath;
        private set => SetProperty(ref _morphHairMeshPath, value);
    }

    private string _morphEditorStatus;
    public string MorphEditorStatus
    {
        get => _morphEditorStatus;
        private set => SetProperty(ref _morphEditorStatus, value);
    }

    private string _morphTargetStatus;
    public string MorphTargetStatus
    {
        get => _morphTargetStatus;
        private set => SetProperty(ref _morphTargetStatus, value);
    }

    private bool _hasMorphEditorData;
    public bool HasMorphEditorData
    {
        get => _hasMorphEditorData;
        private set
        {
            if (SetProperty(ref _hasMorphEditorData, value))
            {
                OnPropertyChanged(nameof(CanOverrideMorph));
                OnPropertyChanged(nameof(CanEditMorph));
                OnPropertyChanged(nameof(CanLoadMorphFaceFx));
            }
        }
    }

    private bool _hasUnsavedMorphChanges;
    public bool HasUnsavedMorphChanges
    {
        get => _hasUnsavedMorphChanges;
        private set => SetProperty(ref _hasUnsavedMorphChanges, value);
    }

    private bool InitializeMorphEditor(ExportEntry morphExport, PackageCache assetCache)
    {
        SuppressMorphEditorChanges = true;
        try
        {
            MorphMaterialPreviewTimer?.Stop();
            MorphTexturePreviewCache.Clear();
            MorphFeatureItems.ClearEx();
            MorphSkeletonItems.ClearEx();
            MorphScalarOverrides.ClearEx();
            MorphColorOverrides.ClearEx();
            MorphTextureOverrides.ClearEx();
            RefreshMorphEditorFilters();
            RemovedMorphBones.Clear();
            MorphTargets = new Dictionary<string, MorphTargetSnapshot>(StringComparer.OrdinalIgnoreCase);
            BaseSkeletonPositions = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

            PropertyCollection properties = morphExport.GetProperties();
            if (properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToExport(morphExport.FileRef, assetCache) is not { } baseHead)
            {
                MorphEditorStatus = "m_oBaseHead does not resolve to a SkeletalMesh export.";
                MorphTargetStatus = "Morph targets unavailable.";
                MorphBaseHeadPath = "Missing m_oBaseHead";
                HasMorphEditorData = false;
                return false;
            }
            if (!baseHead.IsA("SkeletalMesh"))
            {
                MorphEditorStatus = $"m_oBaseHead resolves to {baseHead.ClassName}, not SkeletalMesh.";
                MorphBaseHeadPath = baseHead.InstancedFullPath;
                HasMorphEditorData = false;
                return false;
            }

            MorphBaseHeadExport = baseHead;
            MorphBaseHeadPath = $"m_oBaseHead: {baseHead.InstancedFullPath} ({Path.GetFileName(baseHead.FileRef.FilePath)})";
            MorphHairMeshExport = null;
            MorphHairMeshPath = "m_oHairMesh is not set.";
            if (properties.GetProp<ObjectProperty>("m_oHairMesh") is { Value: not 0 } hairProperty)
            {
                if (hairProperty.ResolveToExport(morphExport.FileRef, assetCache) is { } hairMesh)
                {
                    MorphHairMeshPath = $"m_oHairMesh: {hairMesh.InstancedFullPath} ({Path.GetFileName(hairMesh.FileRef.FilePath)})";
                    if (hairMesh.IsA("SkeletalMesh"))
                    {
                        MorphHairMeshExport = hairMesh;
                    }
                    else
                    {
                        MorphHairMeshPath += $" — {hairMesh.ClassName} cannot be rendered as hair";
                    }
                }
                else
                {
                    MorphHairMeshPath = "m_oHairMesh could not be resolved.";
                }
            }

            BinaryMorphFace binary = ObjectBinary.From<BinaryMorphFace>(morphExport);
            StoredMorphLods = CloneLods(binary?.LODs);
            WorkingMorphLods = CloneLods(StoredMorphLods);

            ArrayProperty<StructProperty> featureProperties = properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures");
            OriginalMorphFeatures = featureProperties?.Select(feature => new MorphFeatureSnapshot(
                    feature.GetProp<NameProperty>("sFeatureName")?.Value.Instanced ?? string.Empty,
                    feature.GetProp<FloatProperty>("Offset")?.Value ?? 0f))
                .ToList() ?? [];
            foreach (MorphFeatureSnapshot feature in OriginalMorphFeatures)
            {
                MorphFeatureItems.Add(new MorphFeatureEditorItem(feature.Name, feature.Value, OnMorphFeatureChanged));
            }
            RefreshMorphFeatureGroups();

            if (properties.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton") is { } skeletonProperties)
            {
                foreach (StructProperty bone in skeletonProperties)
                {
                    string name = bone.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty;
                    Vector3 position = bone.GetProp<StructProperty>("vPos") is { } vector
                        ? CommonStructs.GetVector3(vector)
                        : Vector3.Zero;
                    MorphSkeletonItems.Add(new MorphBoneEditorItem(name, position, OnMorphBoneChanged));
                }
            }

            if (properties.GetProp<ObjectProperty>("m_oMaterialOverrides")?.ResolveToExport(morphExport.FileRef, assetCache) is { } materialOverride)
            {
                LoadMaterialOverrideItems(materialOverride);
            }
            RefreshMorphEditorFilters();

            MorphEditorStatus = StoredMorphLods.Length == 0
                ? "This morph has no stored vertex LODs. Properties can be edited, but no face geometry can be previewed."
                : $"Preparing m_oBaseHead with {StoredMorphLods.Length} morphed face LOD(s)…";
            MorphTargetStatus = "Locating the MorphTargetSet for m_oBaseHead…";
            HasMorphEditorData = true;
            HasUnsavedMorphChanges = false;
            return true;
        }
        finally
        {
            SuppressMorphEditorChanges = false;
        }
    }

    private void LoadMaterialOverrideItems(ExportEntry materialOverride)
    {
        PropertyCollection properties = materialOverride.GetProperties();
        if (properties.GetProp<ArrayProperty<StructProperty>>("m_aScalarOverrides") is { } scalarOverrides)
        {
            foreach (StructProperty scalar in scalarOverrides)
            {
                MorphScalarOverrides.Add(new MorphScalarOverrideItem(
                    scalar.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty,
                    scalar.GetProp<FloatProperty>("sValue")?.Value ?? 0f,
                    OnMorphMaterialChanged));
            }
        }
        if (properties.GetProp<ArrayProperty<StructProperty>>("m_aColorOverrides") is { } colorOverrides)
        {
            foreach (StructProperty color in colorOverrides)
            {
                LinearColor value = color.GetProp<StructProperty>("cValue") is { } linearColor
                    ? CommonStructs.GetLinearColor(linearColor)
                    : LinearColor.White;
                MorphColorOverrides.Add(new MorphColorOverrideItem(
                    color.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty,
                    value, OnMorphMaterialChanged));
            }
        }
        if (properties.GetProp<ArrayProperty<StructProperty>>("m_aTextureOverrides") is { } textureOverrides)
        {
            foreach (StructProperty texture in textureOverrides)
            {
                int uIndex = texture.GetProp<ObjectProperty>("m_pTexture")?.Value ?? 0;
                MorphTextureOverrides.Add(new MorphTextureOverrideItem(
                    texture.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty,
                    uIndex, OnMorphMaterialChanged));
            }
        }
    }

    private PreloadedModelData CreateMorphPreloadedModelData(PackageCache assetCache, ExportEntry baseHead, ExportEntry hairExport)
    {
        var skeletalMesh = ObjectBinary.From<SkeletalMesh>(baseHead);
        MorphPreviewSkeletalMesh = skeletalMesh;
        MorphBindSkeleton = CloneSkeleton(skeletalMesh.RefSkeleton);
        MorphSkinningInfluences = BuildMorphSkinningInfluences(skeletalMesh);
        BaseSkeletonPositions = MorphBindSkeleton
            .GroupBy(bone => bone.Name.Instanced, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Position, StringComparer.OrdinalIgnoreCase);

        ApplyWorkingLodsToSkeletalMesh(skeletalMesh);
        Dictionary<string, Vector3> editedBones = MorphSkeletonItems
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Position, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < skeletalMesh.RefSkeleton.Length; i++)
        {
            if (editedBones.TryGetValue(skeletalMesh.RefSkeleton[i].Name.Instanced, out Vector3 position))
            {
                skeletalMesh.RefSkeleton[i].Position = position;
            }
        }

        PreloadedModelData preloaded = CreateMorphSkeletalMeshPreload(assetCache, baseHead, skeletalMesh);
        if (hairExport is not null)
        {
            try
            {
                var hairMesh = ObjectBinary.From<SkeletalMesh>(hairExport);
                preloaded.additionalModels = [CreateMorphSkeletalMeshPreload(assetCache, hairExport, hairMesh)];
            }
            catch (Exception exception)
            {
                preloaded.additionalModelLoadError = exception.Message;
            }
        }
        return preloaded;
    }

    private static PreloadedModelData CreateMorphSkeletalMeshPreload(PackageCache assetCache, ExportEntry meshExport, SkeletalMesh skeletalMesh)
    {
        var lodMaterialMaps = new List<int[]>();
        if (meshExport.GetProperty<ArrayProperty<StructProperty>>("LODInfo", assetCache) is { } lodInfo)
        {
            foreach (StructProperty lod in lodInfo)
            {
                ArrayProperty<IntProperty> map = lod.GetProp<ArrayProperty<IntProperty>>("LODMaterialMap");
                lodMaterialMaps.Add(map?.Count > 0 ? [.. map.Select(value => value.Value)] : []);
            }
        }

        var preloaded = new PreloadedModelData
        {
            meshObject = skeletalMesh,
            sections = [],
            texturePreviewMaterials = [],
            lodMaterialMaps = lodMaterialMaps,
            additionalModels = []
        };
        IMEPackage package = skeletalMesh.Export.FileRef;
        foreach (int materialIndex in skeletalMesh.Materials.Distinct())
        {
            if (package.TryGetUExport(materialIndex, out ExportEntry materialExport))
            {
                RegisterMorphPreviewMaterial(preloaded.texturePreviewMaterials, materialExport);
            }
            else if (package.TryGetImport(materialIndex, out ImportEntry materialImport)
                     && EntryImporter.ResolveImport(materialImport, assetCache) is { } resolvedMaterial)
            {
                RegisterMorphPreviewMaterial(preloaded.texturePreviewMaterials, resolvedMaterial);
            }
        }
        return preloaded;
    }

    private static void RegisterMorphPreviewMaterial(List<PreloadedTextureData> materials, ExportEntry material)
    {
        if (materials.All(item => item.MaterialExport != material))
        {
            // Morph heads are installed before texture discovery. ModelPreview uses this marker to
            // preserve the SkeletalMesh material index and resolve textures for both preview modes.
            materials.Add(new PreloadedTextureData { MaterialExport = material });
        }
    }

    /// <summary>
    /// Discovers feature targets after the base head has been installed in the viewport. Target lookup
    /// enriches editing, but it must never delay or prevent rendering m_oBaseHead.
    /// </summary>
    private void BeginMorphTargetCatalogLoad(ExportEntry morphSource, ExportEntry baseHead)
    {
        Task.Run(() => LoadMorphTargetCatalog(morphSource, baseHead)).ContinueWithOnUIThread(task =>
        {
            if (!ReferenceEquals(CurrentLoadedExport, morphSource))
            {
                return;
            }
            if (task.IsFaulted || task.IsCanceled)
            {
                Exception exception = task.Exception?.GetBaseException();
                MorphTargetStatus = $"Morph target search failed: {exception?.Message ?? "search was canceled"}";
                return;
            }
            (MorphTargets, PendingMorphTargetStatus) = task.Result;
            CompleteMorphEditorPreviewLoad();
        });
    }

    private (Dictionary<string, MorphTargetSnapshot> Targets, string Status) LoadMorphTargetCatalog(ExportEntry morphSource, ExportEntry baseHead)
    {
        var targets = new Dictionary<string, MorphTargetSnapshot>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ReadPackage(IMEPackage package, bool allowSingleSetFallback)
        {
            if (package is null || !visitedPaths.Add(package.FilePath ?? package.FileNameNoExtension))
            {
                return;
            }
            int before = targets.Count;
            AddMorphTargetsFromPackage(package, baseHead.ObjectName.Name, targets, allowSingleSetFallback);
            if (targets.Count > before)
            {
                sources.Add(Path.GetFileName(package.FilePath));
            }
        }

        ReadPackage(morphSource.FileRef, false);
        ReadPackage(baseHead.FileRef, false);

        string prefix = baseHead.ObjectName.Name.Split('_').FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            try
            {
                string packagePrefix = $"BIOG_{prefix}_";
                IEnumerable<string> candidatePaths = MELoadedFiles
                    .GetFilesLoadedInGame(morphSource.Game, forceUseCached: true)
                    .Where(file => Path.GetFileNameWithoutExtension(file.Key).StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase)
                                   && Path.GetFileNameWithoutExtension(file.Key).Contains("PROMorph", StringComparison.OrdinalIgnoreCase))
                    .Select(file => file.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (string path in candidatePaths)
                {
                    if (visitedPaths.Contains(path))
                    {
                        continue;
                    }
                    using IMEPackage package = MEPackageHandler.OpenMEPackage(path);
                    ReadPackage(package, true);
                }
            }
            catch (Exception exception)
            {
                return (targets, targets.Count > 0
                    ? $"Loaded {targets.Count} morph targets; additional target search failed: {exception.Message}"
                    : $"Morph target search failed: {exception.Message}");
            }
        }

        return targets.Count > 0
            ? (targets, $"Loaded {targets.Count} morph targets from {string.Join(", ", sources.Distinct(StringComparer.OrdinalIgnoreCase))}.")
            : (targets, "No matching MorphTargetSet was found. Feature values remain editable, but only matched targets can deform the preview.");
    }

    private static void AddMorphTargetsFromPackage(IMEPackage package, string baseHeadName,
        Dictionary<string, MorphTargetSnapshot> targets, bool allowSingleSetFallback)
    {
        List<ExportEntry> sets = package.Exports.Where(export => export.ClassName == "MorphTargetSet" && !export.IsDefaultObject).ToList();
        List<ExportEntry> matchingSets = sets.Where(set =>
        {
            ObjectProperty baseProperty = set.GetProperty<ObjectProperty>("BaseSkelMesh");
            IEntry entry = baseProperty?.ResolveToEntry(package);
            return entry?.ObjectName.Name.Equals(baseHeadName, StringComparison.OrdinalIgnoreCase) == true;
        }).ToList();

        if (matchingSets.Count == 0 && allowSingleSetFallback && sets.Count == 1)
        {
            matchingSets = sets;
        }

        using var cache = new PackageCache();
        foreach (ExportEntry set in matchingSets)
        {
            if (set.GetProperty<ArrayProperty<ObjectProperty>>("Targets") is not { } targetProperties)
            {
                continue;
            }
            foreach (ObjectProperty targetProperty in targetProperties)
            {
                if (targetProperty.ResolveToExport(package, cache) is not { ClassName: "MorphTarget" } targetExport)
                {
                    continue;
                }
                MorphTarget binary = ObjectBinary.From<MorphTarget>(targetExport);
                targets.TryAdd(targetExport.ObjectName.Name,
                    new MorphTargetSnapshot(binary.MorphLODModels ?? [], binary.BoneOffsets ?? []));
            }
        }
    }

    private void CompleteMorphEditorPreviewLoad()
    {
        foreach (MorphFeatureEditorItem feature in MorphFeatureItems)
        {
            feature.HasMorphTarget = MorphTargets.ContainsKey(feature.Name);
        }
        RefreshMorphFeatureGroups();
        MorphTargetStatus = PendingMorphTargetStatus;
        if (morphViewportHit != null && MorphViewportPickMode == MorphViewportPickMode.Features) BuildMorphViewportMatches();
        SuppressMorphEditorChanges = true;
        try
        {
            RecalculateMorphFromFeatures();
        }
        finally
        {
            SuppressMorphEditorChanges = false;
        }
        ApplyMorphMaterialOverridePreview();
    }

    private void OnMorphFeatureChanged()
    {
        if (SuppressMorphEditorChanges)
        {
            return;
        }
        bool groupsChanged = false;
        foreach (MorphFeatureEditorItem feature in MorphFeatureItems)
        {
            bool hasMorphTarget = MorphTargets.ContainsKey(feature.Name);
            if (feature.HasMorphTarget != hasMorphTarget)
            {
                feature.HasMorphTarget = hasMorphTarget;
                groupsChanged = true;
            }
        }
        if (groupsChanged)
        {
            RefreshMorphFeatureGroups();
        }
        RecalculateMorphFromFeatures();
        MarkMorphChanged();
    }

    private void RecalculateMorphFromFeatures()
    {
        WorkingMorphLods = CloneLods(StoredMorphLods);
        WorkingMorphNormalDeltas = CreateZeroLods(WorkingMorphLods);
        foreach (MorphFeatureSnapshot original in OriginalMorphFeatures)
        {
            ApplyMorphTargetToWorkingLods(original.Name, -original.Value);
        }
        foreach (MorphFeatureEditorItem current in MorphFeatureItems)
        {
            ApplyMorphTargetToWorkingLods(current.Name, current.Value);
            AccumulateMorphNormalDeltas(current.Name, current.Value);
        }

        var boneDeltas = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        foreach (MorphFeatureSnapshot original in OriginalMorphFeatures)
        {
            AccumulateBoneDeltas(original.Name, -original.Value, boneDeltas);
        }
        foreach (MorphFeatureEditorItem current in MorphFeatureItems)
        {
            AccumulateBoneDeltas(current.Name, current.Value, boneDeltas);
        }

        SuppressMorphEditorChanges = true;
        try
        {
            foreach (MorphBoneEditorItem bone in MorphSkeletonItems)
            {
                bone.SetComputedPosition(bone.BasePosition + boneDeltas.GetValueOrDefault(bone.Name));
            }
            foreach ((string boneName, Vector3 delta) in boneDeltas)
            {
                if (delta.LengthSquared() <= float.Epsilon
                    || RemovedMorphBones.Contains(boneName)
                    || MorphSkeletonItems.Any(item => item.Name.Equals(boneName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                Vector3 basePosition = BaseSkeletonPositions.GetValueOrDefault(boneName);
                MorphSkeletonItems.Add(new MorphBoneEditorItem(boneName, basePosition + delta, OnMorphBoneChanged, basePosition));
            }
        }
        finally
        {
            SuppressMorphEditorChanges = false;
        }
        UpdateMorphSkeletonPreview();
    }

    private void ApplyMorphTargetToWorkingLods(string name, float weight)
    {
        if (weight == 0 || !MorphTargets.TryGetValue(name ?? string.Empty, out MorphTargetSnapshot target))
        {
            return;
        }
        int lodCount = Math.Min(WorkingMorphLods.Length, target.Lods.Length);
        for (int lodIndex = 0; lodIndex < lodCount; lodIndex++)
        {
            if (WorkingMorphLods[lodIndex] is not { } positions || target.Lods[lodIndex]?.Vertices is not { } vertices)
            {
                continue;
            }
            foreach (MorphTarget.MorphVertex vertex in vertices)
            {
                if (vertex.SourceIdx < positions.Length)
                {
                    positions[vertex.SourceIdx] += vertex.PositionDelta * weight;
                }
            }
        }
    }

    private void AccumulateBoneDeltas(string featureName, float weight, Dictionary<string, Vector3> deltas)
    {
        if (weight == 0 || !MorphTargets.TryGetValue(featureName ?? string.Empty, out MorphTargetSnapshot target))
        {
            return;
        }
        foreach (MorphTarget.BoneOffset bone in target.BoneOffsets)
        {
            string name = bone.Bone.Instanced;
            deltas[name] = deltas.GetValueOrDefault(name) + bone.Offset * weight;
        }
    }

    private void AccumulateMorphNormalDeltas(string featureName, float weight)
    {
        if (weight == 0 || !MorphTargets.TryGetValue(featureName ?? string.Empty, out MorphTargetSnapshot target))
        {
            return;
        }
        int lodCount = Math.Min(WorkingMorphNormalDeltas.Length, target.Lods.Length);
        for (int lodIndex = 0; lodIndex < lodCount; lodIndex++)
        {
            if (WorkingMorphNormalDeltas[lodIndex] is not { } normalDeltas
                || target.Lods[lodIndex]?.Vertices is not { } vertices)
            {
                continue;
            }
            foreach (MorphTarget.MorphVertex vertex in vertices)
            {
                if (vertex.SourceIdx < normalDeltas.Length)
                {
                    normalDeltas[vertex.SourceIdx] += (Vector3)vertex.TangentZDelta * weight;
                }
            }
        }
    }

    private void UpdateMorphGeometryPreview(bool currentLodOnly = false)
    {
        if (!currentLodOnly) ApplyWorkingLodsToSkeletalMesh(MorphPreviewSkeletalMesh);
        Matrix4x4[] skinningMatrices = ComputeMorphSkinningMatrices();
        for (int lodIndex = 0; lodIndex < WorkingMorphLods.Length; lodIndex++)
        {
            if (currentLodOnly && lodIndex != CurrentLOD) continue;
            Vector3[] positions = WorkingMorphLods[lodIndex];
            if (positions is null)
            {
                continue;
            }
            if (GameShaderPreview?.LODs.Count > lodIndex)
            {
                Mesh<LEVertex> mesh = GameShaderPreview.LODs[lodIndex].Mesh;
                GPUSkinVertex[] sourceVertices = MorphPreviewSkeletalMesh?.LODModels is { } sourceLods
                                                   && lodIndex < sourceLods.Length
                    ? sourceLods[lodIndex].VertexBufferGPUSkin.VertexData
                    : null;
                Vector3[] normalDeltas = lodIndex < WorkingMorphNormalDeltas.Length
                    ? WorkingMorphNormalDeltas[lodIndex]
                    : null;
                for (int i = 0; i < positions.Length && i < mesh.Vertices.Count; i++)
                {
                    Vector3 skinnedPosition = SkinMorphPosition(positions[i], lodIndex, i, skinningMatrices);
                    Vector4 normal = sourceVertices is not null && i < sourceVertices.Length
                        ? (Vector4)sourceVertices[i].TangentZ
                        : Vector4.UnitZ;
                    if (normalDeltas is not null && i < normalDeltas.Length)
                    {
                        Vector3 deformedNormal = new Vector3(normal.X, normal.Y, normal.Z) + normalDeltas[i];
                        if (deformedNormal.LengthSquared() > float.Epsilon)
                        {
                            deformedNormal = Vector3.Normalize(deformedNormal);
                        }
                        normal = new Vector4(deformedNormal, normal.W);
                    }
                    if (morphFaceFxPoseActive)
                        normal = new Vector4(SkinMorphNormal(new Vector3(normal.X, normal.Y, normal.Z), lodIndex, i, skinningMatrices), normal.W);
                    mesh.Vertices[i] = mesh.Vertices[i].WithPositionAndNormal(
                        CurrentLoadedExport.Game,
                        ToRendererSpace(skinnedPosition),
                        ToRendererSpace(normal));
                }
                mesh.RebuildBuffer(MeshContext.Device);
            }
            if (LEXPreview?.LODs.Count > lodIndex)
            {
                Mesh<WorldVertex> mesh = LEXPreview.LODs[lodIndex].Mesh;
                for (int i = 0; i < positions.Length && i < mesh.Vertices.Count; i++)
                {
                    Vector3 skinnedPosition = SkinMorphPosition(positions[i], lodIndex, i, skinningMatrices);
                    mesh.Vertices[i] = mesh.Vertices[i].WithPosition(ToRendererSpace(skinnedPosition));
                }
                mesh.RebuildBuffer(MeshContext.Device);
            }
        }
        UpdateMorphHairGeometryPreview(currentLodOnly);
        if (ShowSkeleton && morphFaceFxPoseActive && morphFaceFxPlayer != null)
        {
            BuildSkeletonLineBuffer(MorphPreviewSkeletalMesh, morphFaceFxPlayer.BoneComponentSpaceTransforms
                .Select(transform => ToRendererSpace(transform.Translation)).ToArray());
        }
        else if (ShowSkeleton && !currentLodOnly && MorphPreviewSkeletalMesh != null)
        {
            BuildSkeletonLineBuffer(MorphPreviewSkeletalMesh);
        }
    }

    private void UpdateMorphHairGeometryPreview(bool currentLodOnly = false)
    {
        if (MorphPreviewHairMesh is null || MorphHairBindSkeleton.Length == 0)
        {
            return;
        }

        MeshBone[] editedSkeleton = morphFaceFxPoseActive && morphFaceFxHairSkeleton != null
            ? morphFaceFxHairSkeleton : CloneSkeleton(MorphHairBindSkeleton);
        Dictionary<string, Vector3> editedPositions = MorphSkeletonItems
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Position, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < editedSkeleton.Length; i++)
        {
            MeshBone bone = editedSkeleton[i];
            bone.Position = editedPositions.TryGetValue(bone.Name.Instanced, out Vector3 position)
                ? position : MorphHairBindSkeleton[i].Position;
        }

        Matrix4x4[] skinningMatrices;
        if (morphFaceFxPoseActive && morphHairFaceFxPlayer != null)
        {
            morphHairFaceFxPlayer.SetCurrentTime((float)morphFaceFxPosition);
            skinningMatrices = morphHairFaceFxPlayer.ComputeSkinningMatrices();
        }
        else skinningMatrices = ComputeSkinningMatrices(MorphHairBindSkeleton, editedSkeleton);
        for (int lodIndex = 0; lodIndex < MorphHairBindLods.Length; lodIndex++)
        {
            if (currentLodOnly && lodIndex != Math.Min(CurrentLOD, MorphHairBindLods.Length - 1)) continue;
            Vector3[] positions = MorphHairBindLods[lodIndex];
            if (MorphHairGameShaderPreview?.LODs.Count > lodIndex)
            {
                Mesh<LEVertex> mesh = MorphHairGameShaderPreview.LODs[lodIndex].Mesh;
                for (int i = 0; i < positions.Length && i < mesh.Vertices.Count; i++)
                {
                    Vector3 skinnedPosition = SkinPosition(
                        positions[i], lodIndex, i, skinningMatrices, MorphHairSkinningInfluences);
                    mesh.Vertices[i] = mesh.Vertices[i].WithPosition(ToRendererSpace(skinnedPosition));
                }
                mesh.RebuildBuffer(MeshContext.Device);
            }
            if (MorphHairLEXPreview?.LODs.Count > lodIndex)
            {
                Mesh<WorldVertex> mesh = MorphHairLEXPreview.LODs[lodIndex].Mesh;
                for (int i = 0; i < positions.Length && i < mesh.Vertices.Count; i++)
                {
                    Vector3 skinnedPosition = SkinPosition(
                        positions[i], lodIndex, i, skinningMatrices, MorphHairSkinningInfluences);
                    mesh.Vertices[i] = mesh.Vertices[i].WithPosition(ToRendererSpace(skinnedPosition));
                }
                mesh.RebuildBuffer(MeshContext.Device);
            }
        }
    }

    private Matrix4x4[] ComputeMorphSkinningMatrices() =>
        morphFaceFxPoseActive && morphFaceFxPlayer != null
            ? morphFaceFxPlayer.ComputeSkinningMatrices()
            : ComputeSkinningMatrices(MorphBindSkeleton, MorphPreviewSkeletalMesh?.RefSkeleton);

    private static Matrix4x4[] ComputeSkinningMatrices(MeshBone[] bindSkeleton, MeshBone[] editedSkeleton)
        => LegendaryExplorerCore.Unreal.Classes.BioMorphFace.ComputePreviewSkinningMatrices(
            bindSkeleton, editedSkeleton);

    private Vector3 SkinMorphPosition(Vector3 position, int lodIndex, int vertexIndex, Matrix4x4[] skinningMatrices)
        => SkinPosition(position, lodIndex, vertexIndex, skinningMatrices, MorphSkinningInfluences);

    private Vector3 SkinMorphNormal(Vector3 normal, int lod, int vertex, Matrix4x4[] matrices)
    {
        if (lod >= MorphSkinningInfluences.Length || vertex >= MorphSkinningInfluences[lod].Length) return normal;
        MorphSkinInfluences influence = MorphSkinningInfluences[lod][vertex];
        Vector3 result = Transform(influence.Bone0, influence.Weight0)
                         + Transform(influence.Bone1, influence.Weight1)
                         + Transform(influence.Bone2, influence.Weight2)
                         + Transform(influence.Bone3, influence.Weight3);
        return result.LengthSquared() > float.Epsilon ? Vector3.Normalize(result) : normal;

        Vector3 Transform(int bone, float weight) => weight <= 0 ? Vector3.Zero
            : (bone >= 0 && bone < matrices.Length ? Vector3.TransformNormal(normal, matrices[bone]) : normal) * weight;
    }

    private static Vector3 SkinPosition(Vector3 position, int lodIndex, int vertexIndex,
        Matrix4x4[] skinningMatrices, MorphSkinInfluences[][] influences)
    {
        if (skinningMatrices.Length == 0
            || lodIndex >= influences.Length
            || vertexIndex >= influences[lodIndex].Length)
        {
            return position;
        }
        MorphSkinInfluences influence = influences[lodIndex][vertexIndex];
        return LegendaryExplorerCore.Unreal.Classes.BioMorphFace.SkinPreviewPosition(
            position, skinningMatrices,
            influence.Bone0, influence.Weight0,
            influence.Bone1, influence.Weight1,
            influence.Bone2, influence.Weight2,
            influence.Bone3, influence.Weight3);
    }

    private static MorphSkinInfluences[][] BuildMorphSkinningInfluences(SkeletalMesh skeletalMesh)
    {
        var result = new MorphSkinInfluences[skeletalMesh.LODModels.Length][];
        for (int lodIndex = 0; lodIndex < skeletalMesh.LODModels.Length; lodIndex++)
        {
            StaticLODModel lod = skeletalMesh.LODModels[lodIndex];
            GPUSkinVertex[] vertices = lod.VertexBufferGPUSkin.VertexData;
            result[lodIndex] = new MorphSkinInfluences[vertices.Length];
            for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
            {
                SkelMeshChunk chunk = FindMorphVertexChunk(lod, vertexIndex);
                GPUSkinVertex vertex = vertices[vertexIndex];
                float weight0 = vertex.InfluenceWeights[0] / 255f;
                float weight1 = vertex.InfluenceWeights[1] / 255f;
                float weight2 = vertex.InfluenceWeights[2] / 255f;
                float weight3 = vertex.InfluenceWeights[3] / 255f;
                float total = weight0 + weight1 + weight2 + weight3;
                if (total > 0)
                {
                    weight0 /= total;
                    weight1 /= total;
                    weight2 /= total;
                    weight3 /= total;
                }
                else
                {
                    weight0 = 1;
                }
                result[lodIndex][vertexIndex] = new MorphSkinInfluences(
                    ResolveMorphBoneIndex(vertex.InfluenceBones[0], chunk),
                    ResolveMorphBoneIndex(vertex.InfluenceBones[1], chunk),
                    ResolveMorphBoneIndex(vertex.InfluenceBones[2], chunk),
                    ResolveMorphBoneIndex(vertex.InfluenceBones[3], chunk),
                    weight0, weight1, weight2, weight3);
            }
        }
        return result;
    }

    private static SkelMeshChunk FindMorphVertexChunk(StaticLODModel lod, int vertexIndex)
    {
        foreach (SkelMeshChunk chunk in lod.Chunks)
        {
            int start = (int)chunk.BaseVertexIndex;
            int end = start + chunk.NumRigidVertices + chunk.NumSoftVertices;
            if (vertexIndex >= start && vertexIndex < end)
            {
                return chunk;
            }
        }
        return lod.Chunks.FirstOrDefault();
    }

    private static int ResolveMorphBoneIndex(byte influenceBone, SkelMeshChunk chunk) =>
        chunk is not null && influenceBone < chunk.BoneMap.Length ? chunk.BoneMap[influenceBone] : 0;

    private static MeshBone[] CloneSkeleton(MeshBone[] skeleton) => skeleton?.Select(bone => new MeshBone
    {
        Name = bone.Name,
        Flags = bone.Flags,
        Orientation = bone.Orientation,
        Position = bone.Position,
        NumChildren = bone.NumChildren,
        ParentIndex = bone.ParentIndex,
        BoneColor = bone.BoneColor
    }).ToArray() ?? [];

    private static Vector3 ToRendererSpace(Vector3 position) => new(-position.X, position.Z, position.Y);

    private static Vector4 ToRendererSpace(Vector4 vector) => new(-vector.X, vector.Z, vector.Y, vector.W);

    private void ApplyWorkingLodsToSkeletalMesh(SkeletalMesh skeletalMesh)
    {
        if (skeletalMesh?.LODModels is null)
        {
            return;
        }
        int lodCount = Math.Min(skeletalMesh.LODModels.Length, WorkingMorphLods.Length);
        for (int lodIndex = 0; lodIndex < lodCount; lodIndex++)
        {
            Vector3[] positions = WorkingMorphLods[lodIndex];
            GPUSkinVertex[] vertices = skeletalMesh.LODModels[lodIndex].VertexBufferGPUSkin.VertexData;
            for (int i = 0; positions is not null && i < positions.Length && i < vertices.Length; i++)
            {
                GPUSkinVertex vertex = vertices[i];
                vertex.Position = positions[i];
                vertices[i] = vertex;
            }
        }
    }

    private void OnMorphBoneChanged()
    {
        if (SuppressMorphEditorChanges)
        {
            return;
        }
        UpdateMorphSkeletonPreview();
        MarkMorphChanged();
    }

    private void UpdateMorphSkeletonPreview()
    {
        if (MorphPreviewSkeletalMesh?.RefSkeleton is null)
        {
            return;
        }
        Dictionary<string, Vector3> positions = MorphSkeletonItems
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Position, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < MorphPreviewSkeletalMesh.RefSkeleton.Length; i++)
        {
            string boneName = MorphPreviewSkeletalMesh.RefSkeleton[i].Name.Instanced;
            if (positions.TryGetValue(boneName, out Vector3 position))
            {
                MorphPreviewSkeletalMesh.RefSkeleton[i].Position = position;
            }
            else if (BaseSkeletonPositions.TryGetValue(boneName, out Vector3 basePosition))
            {
                MorphPreviewSkeletalMesh.RefSkeleton[i].Position = basePosition;
            }
        }
        UpdateMorphGeometryPreview();
        if (MeshContext.IsReady && !morphFaceFxPoseActive)
        {
            BuildSkeletonLineBuffer(MorphPreviewSkeletalMesh);
        }
    }

    private void OnMorphMaterialChanged()
    {
        if (SuppressMorphEditorChanges)
        {
            return;
        }
        QueueMorphMaterialOverridePreview();
        MarkMorphChanged();
    }

    private void QueueMorphMaterialOverridePreview()
    {
        if (MorphMaterialPreviewTimer is null)
        {
            MorphMaterialPreviewTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            MorphMaterialPreviewTimer.Tick += (_, _) =>
            {
                MorphMaterialPreviewTimer.Stop();
                ApplyMorphMaterialOverridePreview();
            };
        }
        if (!MorphMaterialPreviewTimer.IsEnabled)
        {
            MorphMaterialPreviewTimer.Start();
        }
    }

    private void ApplyMorphMaterialOverridePreview()
    {
        MorphMaterialPreviewTimer?.Stop();
        if (CurrentLoadedExport is null)
        {
            return;
        }
        List<MaterialRenderProxy> materials = GetMorphMaterialPreviewTargets();
        if (materials.Count == 0)
        {
            return;
        }
        foreach (MaterialRenderProxy material in materials)
        {
            material.ResetPreviewParameterOverrides();
            foreach (MorphScalarOverrideItem scalar in MorphScalarOverrides)
            {
                material.SetScalarParameter(scalar.Name, scalar.Value);
            }
            foreach (MorphColorOverrideItem color in MorphColorOverrides)
            {
                material.SetVectorParameter(color.Name, color.Value);
            }
        }

        PackageCache cache = null;
        try
        {
            foreach (MorphTextureOverrideItem texture in MorphTextureOverrides)
            {
                if (!MorphTexturePreviewCache.TryGetValue(texture.EntryIndex, out MorphTexturePreviewResolution resolution))
                {
                    IEntry textureEntry = CurrentLoadedExport.FileRef.GetEntry(texture.EntryIndex);
                    ExportEntry textureExport = textureEntry switch
                    {
                        ExportEntry export when export.IsTexture() => export,
                        ImportEntry import => EntryImporter.ResolveImport(import, cache ??= new PackageCache()),
                        _ => null
                    };
                    if (textureExport is not null && !textureExport.IsTexture())
                    {
                        textureExport = null;
                    }
                    PreviewTextureCache.TextureEntry cachedTexture = textureExport is not null
                        ? MeshContext.TextureCache.LoadTexture(textureExport)
                        : null;
                    string displayPath = textureExport?.InstancedFullPath
                                         ?? (texture.EntryIndex == 0 ? "None" : "Entry is not a resolvable texture");
                    resolution = new MorphTexturePreviewResolution(textureExport, cachedTexture, displayPath);
                    MorphTexturePreviewCache[texture.EntryIndex] = resolution;
                }

                texture.ResolvedPath = resolution.DisplayPath;
                foreach (MaterialRenderProxy material in materials)
                {
                    material.SetTextureParameter(
                        texture.Name, resolution.TextureExport?.InstancedFullPath, resolution.CachedTexture);
                }
            }
        }
        finally
        {
            cache?.Dispose();
        }
    }

    private List<MaterialRenderProxy> GetMorphMaterialPreviewTargets()
    {
        var materials = new List<MaterialRenderProxy>();
        AddPreviewMaterials(GameShaderPreview);
        AddPreviewMaterials(MorphHairGameShaderPreview);
        return materials.Distinct().ToList();

        void AddPreviewMaterials(ModelPreview<LEVertex> preview)
        {
            if (preview is null)
            {
                return;
            }
            materials.AddRange(preview.Materials.Values
                .Select(value => value.Material)
                .OfType<MaterialRenderProxy>());
        }
    }

    private void MarkMorphChanged()
    {
        if (!SuppressMorphEditorChanges)
        {
            HasUnsavedMorphChanges = true;
        }
    }

    private void OverrideMorph_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentLoadedExport is null || !CanOverrideMorph)
        {
            return;
        }
        try
        {
            WriteMorphEditorValues(CurrentLoadedExport);
            HasUnsavedMorphChanges = false;
            MorphEditorStatus = MorphOverrideCompletedOverride?.Invoke(CurrentLoadedExport)
                                ?? $"Overwrote {CurrentLoadedExport.InstancedFullPath}.";
        }
        catch (Exception exception)
        {
            new ExceptionHandlerDialog(exception).ShowDialog();
        }
    }

    private void SaveMorphAsNew_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentLoadedExport is null || !CanEditMorph)
        {
            return;
        }
        string suggestedName = $"{CurrentLoadedExport.ObjectName.Name}_Edited";
        string name = PromptDialog.Prompt(this, "Name the new BioMorphFace:", "Make new morph", suggestedName,
            selectText: true, validator: MorphNewNameValidatorOverride ?? ValidateNewMorphName);
        if (name is null)
        {
            return;
        }
        try
        {
            string trimmedName = name.Trim();
            ExportEntry clone = MorphSaveTargetCreatorOverride?.Invoke(CurrentLoadedExport, trimmedName)
                                ?? EntryCloner.CloneTree(CurrentLoadedExport);
            clone.ObjectName = new NameReference(trimmedName);
            EnsureNewMorphHasIndependentMaterialOverride(clone);
            WriteMorphEditorValues(clone);
            HasUnsavedMorphChanges = false;
            MorphEditorStatus = MorphSaveAsNewCompletedOverride?.Invoke(clone)
                                ?? $"Created export {clone.UIndex}: {clone.InstancedFullPath}.";
        }
        catch (Exception exception)
        {
            new ExceptionHandlerDialog(exception).ShowDialog();
        }
    }

    private (bool, string) ValidateNewMorphName(string value)
    {
        string name = value?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return (false, "Enter a morph name.");
        }
        if (!(char.IsLetter(name[0]) || name[0] == '_') || name.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            return (false, "Use letters, numbers, and underscores; the first character cannot be a number.");
        }
        string path = CurrentLoadedExport.Parent is { } parent ? $"{parent.InstancedFullPath}.{name}" : name;
        return CurrentLoadedExport.FileRef.FindEntry(path) is null
            ? (true, null)
            : (false, "An entry with that name already exists here.");
    }

    private void WriteMorphEditorValues(ExportEntry target)
    {
        PropertyCollection properties = target.GetProperties();
        properties.RemoveNamedProperty("m_aMorphFeatures");
        properties.RemoveNamedProperty("m_aFinalSkeleton");

        properties.Add(new ArrayProperty<StructProperty>(MorphFeatureItems.Select(feature =>
            new StructProperty("MorphFeature", false,
                new NameProperty(feature.Name, "sFeatureName"),
                new FloatProperty(feature.Value, "Offset"))), "m_aMorphFeatures"));
        properties.Add(new ArrayProperty<StructProperty>(MorphSkeletonItems.Select(bone =>
            new StructProperty("OffsetBonePos", false,
                new NameProperty(bone.Name, "nName"),
                CommonStructs.Vector3Prop(bone.Position, "vPos"))), "m_aFinalSkeleton"));

        ExportEntry materialOverride = EnsureMaterialOverride(target, properties);
        WriteMaterialOverrideValues(materialOverride);

        var binary = ObjectBinary.From<BinaryMorphFace>(target) ?? BinaryMorphFace.Create();
        binary.LODs = CloneLods(WorkingMorphLods);
        target.WritePropertiesAndBinary(properties, binary);
    }

    private static ExportEntry EnsureMaterialOverride(ExportEntry morph, PropertyCollection morphProperties)
    {
        using var cache = new PackageCache();
        ObjectProperty overrideProperty = morphProperties.GetProp<ObjectProperty>("m_oMaterialOverrides");
        if (overrideProperty?.ResolveToEntry(morph.FileRef) is ExportEntry { FileRef: var fileRef } local
            && ReferenceEquals(fileRef, morph.FileRef)
            && local.Parent == morph)
        {
            return local;
        }
        ExportEntry source = overrideProperty?.ResolveToExport(morph.FileRef, cache);
        ExportEntry created = ExportCreator.CreateExport(morph.FileRef, "BioMaterialOverride", "BioMaterialOverride", morph, indexed: false);
        if (source is not null)
        {
            created.WriteProperties(source.GetProperties());
        }
        morphProperties.AddOrReplaceProp(new ObjectProperty(created, "m_oMaterialOverrides"));
        return created;
    }

    private static void EnsureNewMorphHasIndependentMaterialOverride(ExportEntry morph)
    {
        PropertyCollection properties = morph.GetProperties();
        using var cache = new PackageCache();
        ExportEntry existing = properties.GetProp<ObjectProperty>("m_oMaterialOverrides")?.ResolveToExport(morph.FileRef, cache);
        if (existing is null || existing.Parent == morph)
        {
            return;
        }
        ExportEntry created = ExportCreator.CreateExport(morph.FileRef, "BioMaterialOverride", "BioMaterialOverride", morph, indexed: false);
        created.WriteProperties(existing.GetProperties());
        properties.AddOrReplaceProp(new ObjectProperty(created, "m_oMaterialOverrides"));
        morph.WriteProperties(properties);
    }

    private void WriteMaterialOverrideValues(ExportEntry materialOverride)
    {
        PropertyCollection properties = materialOverride.GetProperties();
        properties.RemoveNamedProperty("m_aScalarOverrides");
        properties.RemoveNamedProperty("m_aColorOverrides");
        properties.RemoveNamedProperty("m_aTextureOverrides");

        properties.Add(new ArrayProperty<StructProperty>(MorphScalarOverrides.Select(scalar =>
            new StructProperty("ScalarParameter", false,
                new NameProperty(scalar.Name, "nName"),
                new FloatProperty(scalar.Value, "sValue"))), "m_aScalarOverrides"));
        properties.Add(new ArrayProperty<StructProperty>(MorphColorOverrides.Select(color =>
            new StructProperty("ColorParameter", false,
                new NameProperty(color.Name, "nName"),
                CommonStructs.LinearColorProp(color.R, color.G, color.B, color.A, "cValue"))), "m_aColorOverrides"));
        properties.Add(new ArrayProperty<StructProperty>(MorphTextureOverrides.Select(texture =>
            new StructProperty("TextureParameter", false,
                new NameProperty(texture.Name, "nName"),
                new ObjectProperty(texture.EntryIndex, "m_pTexture"))), "m_aTextureOverrides"));
        materialOverride.WriteProperties(properties);
    }

    private void AddMorphFeature_Click(object sender, RoutedEventArgs e)
    {
        var existingFeatures = MorphFeatureItems
            .Select(feature => feature.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] addableFeatures = MorphTargets.Keys
            .Where(name => !existingFeatures.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (addableFeatures.Length == 0)
        {
            MorphTargetStatus = MorphTargets.Count == 0
                ? "No morph targets are available to add."
                : "Every available morph target is already present on this face.";
            return;
        }

        string name = StringSelectorDialog.GetValue(this,
            $"Choose a morph feature to add. Type to search the {addableFeatures.Length} available targets.",
            "Add morph feature", addableFeatures);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var item = new MorphFeatureEditorItem(name, 0, OnMorphFeatureChanged)
        {
            HasMorphTarget = true
        };
        MorphFeatureItems.Add(item);
        RefreshMorphFeatureGroups();
        OnMorphFeatureChanged();
    }

    private async void RandomizeMorphFeatures_Click(object sender, RoutedEventArgs e)
    {
        if (MorphBaseHeadExport is null)
        {
            MorphEditorStatus = "m_oBaseHead must finish loading before the face can be randomized.";
            return;
        }

        float strength = MorphRandomizationStrength;
        if (strength <= 0f)
        {
            MorphEditorStatus = "Randomization strength is 0%; the current morph and material values were left unchanged.";
            return;
        }

        BioMorphSpecies species = MorphBaseHeadExport.ObjectName.Name.GetBioMorphSpecies();
        if (species == BioMorphSpecies.Unknown)
        {
            MorphEditorStatus = $"Could not identify the species from m_oBaseHead {MorphBaseHeadExport.ObjectName.Instanced}.";
            return;
        }

        ExportEntry sourceMorph = CurrentLoadedExport;
        IsBusy = true;
        MEGame game = sourceMorph.Game;
        BusyText = $"Loading BioWare {species.ToDisplayName()} faces from the {game} Asset Database…";
        BioMorphReferenceCatalog catalog;
        try
        {
            catalog = await BioMorphRandomizationCatalog.GetCatalogAsync(game, species);
        }
        catch (Exception exception)
        {
            MorphEditorStatus = $"Could not load morph randomization references: {exception.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (!ReferenceEquals(CurrentLoadedExport, sourceMorph))
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(catalog.Error))
        {
            MorphEditorStatus = catalog.Error;
            return;
        }

        List<BioMorphReferenceFace> materialFaces = catalog.Faces
            .Where(face => face.ScalarOverrides.Count > 0 || face.ColorOverrides.Count > 0)
            .ToList();
        List<BioMorphReferenceFace> exactMaterialFaces = materialFaces
            .Where(face => face.BaseHeadName.Equals(MorphBaseHeadExport.ObjectName.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactMaterialFaces.Count > 0)
        {
            materialFaces = exactMaterialFaces;
        }
        if (materialFaces.Count == 0)
        {
            MorphEditorStatus = $"The Asset Database did not provide a compatible {species.ToDisplayName()} scalar/color material profile.";
            return;
        }

        BioMorphReferenceFace firstMaterialFace = materialFaces[Random.Shared.Next(materialFaces.Count)];
        BioMorphReferenceFace secondMaterialFace = firstMaterialFace;
        if (materialFaces.Count > 1)
        {
            do
            {
                secondMaterialFace = materialFaces[Random.Shared.Next(materialFaces.Count)];
            } while (ReferenceEquals(firstMaterialFace, secondMaterialFace));
        }

        float firstWeight = 0.4f + Random.Shared.NextSingle() * 0.2f;
        float secondWeight = 1f - firstWeight;
        Dictionary<string, float> randomizedScalars = BlendMorphScalarOverrides(
            firstMaterialFace, secondMaterialFace, firstWeight, secondWeight);
        Dictionary<string, LinearColor> randomizedColors = BlendMorphColorOverrides(
            firstMaterialFace, secondMaterialFace, firstWeight, secondWeight);

        Dictionary<string, float> randomizedValues = null;
        HashSet<string> availableTargets = MorphTargets.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int requiredTargetMatches = Math.Min(3, availableTargets.Count);
        if (requiredTargetMatches > 0)
        {
            List<BioMorphReferenceFace> geometryFaces = catalog.Faces
                .Where(face => face.Features.Keys.Count(availableTargets.Contains) >= requiredTargetMatches)
                .ToList();
            List<BioMorphReferenceFace> exactGeometryFaces = geometryFaces
                .Where(face => face.BaseHeadName.Equals(MorphBaseHeadExport.ObjectName.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exactGeometryFaces.Count >= 2)
            {
                geometryFaces = exactGeometryFaces;
            }
            if (geometryFaces.Count >= 2)
            {
                BioMorphReferenceFace firstGeometryFace = geometryFaces[Random.Shared.Next(geometryFaces.Count)];
                BioMorphReferenceFace secondGeometryFace;
                do
                {
                    secondGeometryFace = geometryFaces[Random.Shared.Next(geometryFaces.Count)];
                } while (ReferenceEquals(firstGeometryFace, secondGeometryFace));

                randomizedValues = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (string targetName in availableTargets)
                {
                    float value = firstGeometryFace.Features.GetValueOrDefault(targetName) * firstWeight
                                  + secondGeometryFace.Features.GetValueOrDefault(targetName) * secondWeight;
                    randomizedValues[targetName] = Math.Abs(value) < 0.0001f ? 0f : value;
                }
            }
        }

        SuppressMorphEditorChanges = true;
        try
        {
            if (randomizedValues is not null)
            {
                Dictionary<string, List<MorphFeatureEditorItem>> existingFeatures = MorphFeatureItems
                    .Where(feature => feature.HasMorphTarget)
                    .GroupBy(feature => feature.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach ((string targetName, float value) in randomizedValues)
                {
                    if (existingFeatures.TryGetValue(targetName, out List<MorphFeatureEditorItem> features))
                    {
                        float currentValue = features.Sum(feature => feature.Value);
                        features[0].Value = ApplyMorphRandomizationStrength(currentValue, value, strength);
                        foreach (MorphFeatureEditorItem duplicate in features.Skip(1))
                        {
                            duplicate.Value = 0;
                        }
                    }
                    else if (value != 0)
                    {
                        float randomizedValue = ApplyMorphRandomizationStrength(0f, value, strength);
                        if (Math.Abs(randomizedValue) < 0.0001f)
                        {
                            continue;
                        }
                        MorphFeatureItems.Add(new MorphFeatureEditorItem(targetName, randomizedValue, OnMorphFeatureChanged)
                        {
                            HasMorphTarget = true
                        });
                    }
                }
            }

            List<MaterialRenderProxy> previewMaterials = GetMorphMaterialPreviewTargets();
            Dictionary<string, float> effectiveScalars = previewMaterials
                .SelectMany(material => material.ScalarParameters)
                .GroupBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MorphScalarOverrideItem> existingScalars = MorphScalarOverrides
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            foreach ((string name, float value) in randomizedScalars.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (existingScalars.TryGetValue(name, out MorphScalarOverrideItem existing))
                {
                    existing.Value = ApplyMorphRandomizationStrength(existing.Value, value, strength);
                }
                else
                {
                    float currentValue = effectiveScalars.GetValueOrDefault(name, value);
                    MorphScalarOverrides.Add(new MorphScalarOverrideItem(
                        name, ApplyMorphRandomizationStrength(currentValue, value, strength), OnMorphMaterialChanged));
                }
            }
            Dictionary<string, LinearColor> effectiveColors = previewMaterials
                .SelectMany(material => material.VectorParameters)
                .GroupBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MorphColorOverrideItem> existingColors = MorphColorOverrides
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            foreach ((string name, LinearColor value) in randomizedColors.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (existingColors.TryGetValue(name, out MorphColorOverrideItem existing))
                {
                    LinearColor randomizedValue = ApplyMorphRandomizationStrength(existing.Value, value, strength);
                    existing.R = randomizedValue.R;
                    existing.G = randomizedValue.G;
                    existing.B = randomizedValue.B;
                    existing.A = randomizedValue.A;
                }
                else
                {
                    LinearColor currentValue = effectiveColors.GetValueOrDefault(name, value);
                    MorphColorOverrides.Add(new MorphColorOverrideItem(
                        name, ApplyMorphRandomizationStrength(currentValue, value, strength), OnMorphMaterialChanged));
                }
            }
        }
        finally
        {
            SuppressMorphEditorChanges = false;
        }

        RefreshMorphEditorFilters();
        if (randomizedValues is not null)
        {
            OnMorphFeatureChanged();
        }
        OnMorphMaterialChanged();
        string geometryStatus = randomizedValues is null
            ? "Morph features were left unchanged because no compatible targets were found."
            : $"Randomized {randomizedValues.Count(value => value.Value != 0)} morph features.";
        MorphEditorStatus = $"Randomized at {strength:P0} strength: {randomizedScalars.Count} {species.ToDisplayName()} scalar overrides and {randomizedColors.Count} color overrides from {materialFaces.Count} Asset Database material profiles. {geometryStatus} Texture overrides were left unchanged.";
    }

    private static float ApplyMorphRandomizationStrength(float currentValue, float randomizedValue, float strength) =>
        currentValue + (randomizedValue - currentValue) * strength;

    private static LinearColor ApplyMorphRandomizationStrength(
        LinearColor currentValue, LinearColor randomizedValue, float strength) => new(
        ApplyMorphRandomizationStrength(currentValue.R, randomizedValue.R, strength),
        ApplyMorphRandomizationStrength(currentValue.G, randomizedValue.G, strength),
        ApplyMorphRandomizationStrength(currentValue.B, randomizedValue.B, strength),
        ApplyMorphRandomizationStrength(currentValue.A, randomizedValue.A, strength));

    private static Dictionary<string, float> BlendMorphScalarOverrides(
        BioMorphReferenceFace firstFace, BioMorphReferenceFace secondFace, float firstWeight, float secondWeight)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in firstFace.ScalarOverrides.Keys.Concat(secondFace.ScalarOverrides.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool hasFirst = firstFace.ScalarOverrides.TryGetValue(name, out float firstValue);
            bool hasSecond = secondFace.ScalarOverrides.TryGetValue(name, out float secondValue);
            result[name] = hasFirst && hasSecond
                ? firstValue * firstWeight + secondValue * secondWeight
                : hasFirst ? firstValue : secondValue;
        }
        return result;
    }

    private static Dictionary<string, LinearColor> BlendMorphColorOverrides(
        BioMorphReferenceFace firstFace, BioMorphReferenceFace secondFace, float firstWeight, float secondWeight)
    {
        var result = new Dictionary<string, LinearColor>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in firstFace.ColorOverrides.Keys.Concat(secondFace.ColorOverrides.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool hasFirst = firstFace.ColorOverrides.TryGetValue(name, out BioMorphReferenceColor firstValue);
            bool hasSecond = secondFace.ColorOverrides.TryGetValue(name, out BioMorphReferenceColor secondValue);
            BioMorphReferenceColor value = hasFirst && hasSecond
                ? new BioMorphReferenceColor(
                    firstValue.R * firstWeight + secondValue.R * secondWeight,
                    firstValue.G * firstWeight + secondValue.G * secondWeight,
                    firstValue.B * firstWeight + secondValue.B * secondWeight,
                    firstValue.A * firstWeight + secondValue.A * secondWeight)
                : hasFirst ? firstValue : secondValue;
            result[name] = new LinearColor(value.R, value.G, value.B, value.A);
        }
        return result;
    }

    private void RemoveMorphFeature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MorphFeatureEditorItem item } && MorphFeatureItems.Remove(item))
        {
            RefreshMorphFeatureGroups();
            OnMorphFeatureChanged();
        }
    }

    private string LoadMorphHairPreview(PreloadedModelData hairData, PackageCache assetCache, string loadError)
    {
        DisposeMorphHairPreview();
        if (MorphHairMeshExport is null)
        {
            return MorphHairMeshPath;
        }
        if (!string.IsNullOrWhiteSpace(loadError))
        {
            return $"Could not load m_oHairMesh: {loadError}";
        }
        if (hairData?.meshObject is not SkeletalMesh hairMesh)
        {
            return "m_oHairMesh resolved, but no preview data was produced.";
        }

        try
        {
            MorphPreviewHairMesh = hairMesh;
            MorphHairBindSkeleton = CloneSkeleton(hairMesh.RefSkeleton);
            MorphHairSkinningInfluences = BuildMorphSkinningInfluences(hairMesh);
            MorphHairBindLods = hairMesh.LODModels
                .Select(lod => lod.VertexBufferGPUSkin.VertexData.Select(vertex => vertex.Position).ToArray())
                .ToArray();

            if (CanUseGameShaders && RenderGameShader)
            {
                MorphHairGameShaderPreview = new ModelPreview<LEVertex>(
                    MeshContext.Device, hairMesh, MeshContext.TextureCache, assetCache, hairData);
            }
            MorphHairLEXPreview = new ModelPreview<WorldVertex>(
                MeshContext.Device, hairMesh, MeshContext.TextureCache, assetCache, hairData);

            int lodCount = RenderGameShader
                ? MorphHairGameShaderPreview?.LODs.Count ?? 0
                : MorphHairLEXPreview?.LODs.Count ?? 0;
            return $"Rendered m_oHairMesh with {lodCount} LOD(s).";
        }
        catch (Exception exception)
        {
            DisposeMorphHairPreview();
            return $"Could not render m_oHairMesh: {exception.Message}";
        }
    }

    private void RenderMorphHairPreview(RenderPass renderPass)
    {
        if (HideMorphHair)
        {
            return;
        }
        if (RenderSolid && MorphHairLEXPreview is { LODs.Count: > 0 } standardPreview)
        {
            MeshContext.Wireframe = false;
            int lodIndex = Math.Clamp(CurrentLOD, 0, standardPreview.LODs.Count - 1);
            standardPreview.Render(renderPass, MeshContext, lodIndex, Matrix4x4.Identity);
        }
        if (RenderGameShader && MorphHairGameShaderPreview is { LODs.Count: > 0 } gamePreview)
        {
            MeshContext.Wireframe = false;
            int lodIndex = Math.Clamp(CurrentLOD, 0, gamePreview.LODs.Count - 1);
            gamePreview.Render(renderPass, MeshContext, lodIndex, Matrix4x4.Identity);
        }
    }

    private void RenderMorphHairWireframe()
    {
        if (HideMorphHair || !RenderWireframe || MorphHairLEXPreview is not { LODs.Count: > 0 } preview)
        {
            return;
        }
        int lodIndex = Math.Clamp(CurrentLOD, 0, preview.LODs.Count - 1);
        MeshContext.Wireframe = true;
        var viewConstants = new MeshRenderContext.WorldConstants(
            Matrix4x4.Transpose(MeshContext.Camera.ProjectionMatrix),
            Matrix4x4.Transpose(MeshContext.Camera.ViewMatrix),
            Matrix4x4.Identity,
            MeshContext.CurrentTextureViewFlags);
        MeshContext.DefaultEffect.PrepDraw(MeshContext.ImmediateContext, MeshContext.AlphaBlendState);
        MeshContext.DefaultEffect.RenderObject(
            MeshContext.ImmediateContext, viewConstants, preview.LODs[lodIndex].Mesh, [null]);
    }

    private void DisposeMorphHairPreview()
    {
        MorphHairLEXPreview?.Dispose();
        MorphHairLEXPreview = null;
        MorphHairGameShaderPreview?.Dispose();
        MorphHairGameShaderPreview = null;
        MorphPreviewHairMesh = null;
        MorphHairBindSkeleton = [];
        MorphHairSkinningInfluences = [];
        MorphHairBindLods = [];
    }

    private void RefreshMorphFeatureGroups()
    {
        OnPropertyChanged(nameof(MatchedMorphFeatureItems));
        OnPropertyChanged(nameof(UnmatchedMorphFeatureItems));
        OnPropertyChanged(nameof(UnmatchedMorphFeatureCount));
        OnPropertyChanged(nameof(HasUnmatchedMorphFeatures));
    }

    private bool MatchesMorphEditorSearch(params string[] values)
    {
        string search = MorphEditorSearchText?.Trim();
        return string.IsNullOrWhiteSpace(search)
               || values.Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void RefreshMorphEditorFilters()
    {
        RefreshMorphFeatureGroups();
        OnPropertyChanged(nameof(FilteredMorphSkeletonItems));
        OnPropertyChanged(nameof(FilteredMorphScalarOverrides));
        OnPropertyChanged(nameof(FilteredMorphColorOverrides));
        OnPropertyChanged(nameof(FilteredMorphTextureOverrides));
    }

    private void AddMorphBone_Click(object sender, RoutedEventArgs e)
    {
        string name = PromptDialog.Prompt(this, "Bone name:", "Add final-skeleton bone");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        string trimmed = name.Trim();
        Vector3 position = BaseSkeletonPositions.GetValueOrDefault(trimmed);
        RemovedMorphBones.Remove(trimmed);
        MorphSkeletonItems.Add(new MorphBoneEditorItem(trimmed, position, OnMorphBoneChanged));
        RefreshMorphEditorFilters();
        MarkMorphChanged();
        UpdateMorphSkeletonPreview();
    }

    private void RemoveMorphBone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MorphBoneEditorItem item } && MorphSkeletonItems.Remove(item))
        {
            RemovedMorphBones.Add(item.Name);
            RefreshMorphEditorFilters();
            MarkMorphChanged();
            UpdateMorphSkeletonPreview();
        }
    }

    private void AddMorphScalar_Click(object sender, RoutedEventArgs e)
    {
        List<MaterialRenderProxy> materials = GetMorphMaterialSelectionTargets();
        var existingNames = MorphScalarOverrides.Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = materials
            .SelectMany(material => material.ScalarParameters.Select(parameter => (
                Name: parameter.Key,
                Value: parameter.Value,
                MaterialName: material.Export.ObjectName.Instanced)))
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name) && !existingNames.Contains(parameter.Name))
            .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0)
        {
            MorphEditorStatus = materials.Count == 0
                ? "No in-game shader materials are loaded for scalar parameter discovery."
                : "Every scalar parameter exposed by the head and hair materials is already overridden.";
            return;
        }

        var choices = groups.Select(group => new StringSelectorItem(
            group.Key,
            group.Key,
            $"Used by {string.Join(", ", group.Select(item => item.MaterialName).Distinct(StringComparer.OrdinalIgnoreCase))}"));
        string selectedName = StringSelectorDialog.GetValue(this,
            $"Choose a scalar parameter to override. Type to search the {groups.Count} available values.",
            "Add scalar override", choices);
        if (string.IsNullOrWhiteSpace(selectedName))
        {
            return;
        }

        float initialValue = groups.First(group => group.Key.Equals(selectedName, StringComparison.OrdinalIgnoreCase)).First().Value;
        MorphScalarOverrides.Add(new MorphScalarOverrideItem(selectedName, initialValue, OnMorphMaterialChanged));
        CompleteMaterialItemAddition();
    }

    private void AddMorphColor_Click(object sender, RoutedEventArgs e)
    {
        List<MaterialRenderProxy> materials = GetMorphMaterialSelectionTargets();
        var existingNames = MorphColorOverrides.Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = materials
            .SelectMany(material => material.VectorParameters.Select(parameter => (
                Name: parameter.Key,
                Value: parameter.Value,
                MaterialName: material.Export.ObjectName.Instanced)))
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name) && !existingNames.Contains(parameter.Name))
            .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0)
        {
            MorphEditorStatus = materials.Count == 0
                ? "No in-game shader materials are loaded for color parameter discovery."
                : "Every color parameter exposed by the head and hair materials is already overridden.";
            return;
        }

        var choices = groups.Select(group => new StringSelectorItem(
            group.Key,
            group.Key,
            $"Used by {string.Join(", ", group.Select(item => item.MaterialName).Distinct(StringComparer.OrdinalIgnoreCase))}"));
        string selectedName = StringSelectorDialog.GetValue(this,
            $"Choose a color parameter to override. Type to search the {groups.Count} available values.",
            "Add color override", choices);
        if (string.IsNullOrWhiteSpace(selectedName))
        {
            return;
        }

        LinearColor initialValue = groups.First(group => group.Key.Equals(selectedName, StringComparison.OrdinalIgnoreCase)).First().Value;
        MorphColorOverrides.Add(new MorphColorOverrideItem(selectedName, initialValue, OnMorphMaterialChanged));
        CompleteMaterialItemAddition();
    }

    private void AddMorphTexture_Click(object sender, RoutedEventArgs e) => AddMaterialItem(
        "Texture parameter name:", name => MorphTextureOverrides.Add(new MorphTextureOverrideItem(name, 0, OnMorphMaterialChanged)));

    private void AddMaterialItem(string prompt, Action<string> add)
    {
        string name = PromptDialog.Prompt(this, prompt, "Add material override");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        add(name.Trim());
        CompleteMaterialItemAddition();
    }

    private void CompleteMaterialItemAddition()
    {
        RefreshMorphEditorFilters();
        OnMorphMaterialChanged();
    }

    private void RemoveMorphScalar_Click(object sender, RoutedEventArgs e) => RemoveMaterialItem(sender, MorphScalarOverrides);
    private void RemoveMorphColor_Click(object sender, RoutedEventArgs e) => RemoveMaterialItem(sender, MorphColorOverrides);
    private void RemoveMorphTexture_Click(object sender, RoutedEventArgs e) => RemoveMaterialItem(sender, MorphTextureOverrides);

    private void RemoveMaterialItem<T>(object sender, ObservableCollectionExtended<T> collection)
    {
        if (sender is FrameworkElement { DataContext: T item } && collection.Remove(item))
        {
            RefreshMorphEditorFilters();
            OnMorphMaterialChanged();
        }
    }

    private void UnloadMorphEditor()
    {
        if (!IsMorphEditorMode)
        {
            return;
        }
        ClearMorphViewportSelection();
        UnloadMorphFaceFx();
        DisposeMorphHairPreview();
        MorphMaterialPreviewTimer?.Stop();
        MorphTexturePreviewCache.Clear();
        SuppressMorphEditorChanges = true;
        try
        {
            MorphFeatureItems.ClearEx();
            MorphSkeletonItems.ClearEx();
            MorphScalarOverrides.ClearEx();
            MorphColorOverrides.ClearEx();
            MorphTextureOverrides.ClearEx();
            MorphEditorSearchText = null;
            RefreshMorphEditorFilters();
            RemovedMorphBones.Clear();
            MorphTargets.Clear();
            BaseSkeletonPositions.Clear();
            OriginalMorphFeatures.Clear();
            StoredMorphLods = [];
            WorkingMorphLods = [];
            WorkingMorphNormalDeltas = [];
            MorphBindSkeleton = [];
            MorphSkinningInfluences = [];
            MorphBaseHeadExport = null;
            MorphHairMeshExport = null;
            MorphPreviewSkeletalMesh = null;
            MorphBaseHeadPath = null;
            MorphHairMeshPath = null;
            MorphTargetStatus = null;
            MorphEditorStatus = null;
            HasMorphEditorData = false;
            HasUnsavedMorphChanges = false;
        }
        finally
        {
            SuppressMorphEditorChanges = false;
        }
    }

    private static Vector3[][] CloneLods(Vector3[][] source) =>
        source?.Select(lod => lod is null ? null : (Vector3[])lod.Clone()).ToArray() ?? [];

    private static Vector3[][] CreateZeroLods(Vector3[][] source) =>
        source?.Select(lod => lod is null ? null : new Vector3[lod.Length]).ToArray() ?? [];
}

public sealed class MorphFeatureEditorItem : NotifyPropertyChangedBase
{
    private readonly Action Changed;
    private string _name;
    private float _value;
    private bool _hasMorphTarget;

    public string Name { get => _name; set { if (SetProperty(ref _name, value ?? string.Empty)) Changed(); } }
    public float Value { get => _value; set { if (float.IsFinite(value) && SetProperty(ref _value, value)) Changed(); } }
    public float Minimum => Math.Min(-1f, Value - 1f);
    public float Maximum => Math.Max(1f, Value + 1f);
    public bool HasMorphTarget { get => _hasMorphTarget; internal set => SetProperty(ref _hasMorphTarget, value); }

    public MorphFeatureEditorItem(string name, float value, Action changed)
    {
        _name = name;
        _value = value;
        Changed = changed;
    }
}

public sealed class MorphBoneEditorItem : NotifyPropertyChangedBase
{
    private bool isViewportSelected;
    public bool IsViewportSelected { get => isViewportSelected; internal set => SetProperty(ref isViewportSelected, value); }
    private readonly Action Changed;
    private string _name;
    private Vector3 ComputedPosition;
    private Vector3 ManualOffset;
    private float _x;
    private float _y;
    private float _z;

    public string Name { get => _name; set { if (SetProperty(ref _name, value ?? string.Empty)) Changed(); } }
    public Vector3 BasePosition { get; }
    public Vector3 Position => new(X, Y, Z);
    public float Minimum => Math.Min(-25f, Math.Min(X, Math.Min(Y, Z)) - 10f);
    public float Maximum => Math.Max(25f, Math.Max(X, Math.Max(Y, Z)) + 10f);
    public float X { get => _x; set => SetComponent(ref _x, value, 0); }
    public float Y { get => _y; set => SetComponent(ref _y, value, 1); }
    public float Z { get => _z; set => SetComponent(ref _z, value, 2); }

    public MorphBoneEditorItem(string name, Vector3 position, Action changed, Vector3? basePosition = null)
    {
        _name = name;
        BasePosition = basePosition ?? position;
        ComputedPosition = position;
        _x = position.X;
        _y = position.Y;
        _z = position.Z;
        Changed = changed;
    }

    internal void SetComputedPosition(Vector3 position)
    {
        ComputedPosition = position;
        Vector3 displayed = position + ManualOffset;
        SetProperty(ref _x, displayed.X, nameof(X));
        SetProperty(ref _y, displayed.Y, nameof(Y));
        SetProperty(ref _z, displayed.Z, nameof(Z));
    }

    private void SetComponent(ref float field, float value, int component)
    {
        if (!float.IsFinite(value) || !SetProperty(ref field, value))
        {
            return;
        }
        ManualOffset = component switch
        {
            0 => ManualOffset with { X = value - ComputedPosition.X },
            1 => ManualOffset with { Y = value - ComputedPosition.Y },
            _ => ManualOffset with { Z = value - ComputedPosition.Z }
        };
        Changed();
    }
}

public sealed class MorphScalarOverrideItem : NotifyPropertyChangedBase
{
    private readonly Action Changed;
    private string _name;
    private float _value;
    public string Name { get => _name; set { if (SetProperty(ref _name, value ?? string.Empty)) Changed(); } }
    public float Value { get => _value; set { if (float.IsFinite(value) && SetProperty(ref _value, value)) Changed(); } }
    public float Minimum => Math.Min(-1f, Value - 1f);
    public float Maximum => Math.Max(1f, Value + 1f);

    public MorphScalarOverrideItem(string name, float value, Action changed)
    {
        _name = name;
        _value = value;
        Changed = changed;
    }
}

public sealed class MorphColorOverrideItem : NotifyPropertyChangedBase
{
    private readonly Action Changed;
    private string _name;
    private float _r;
    private float _g;
    private float _b;
    private float _a;
    public string Name { get => _name; set { if (SetProperty(ref _name, value ?? string.Empty)) Changed(); } }
    public float R { get => _r; set => SetComponent(ref _r, value); }
    public float G { get => _g; set => SetComponent(ref _g, value); }
    public float B { get => _b; set => SetComponent(ref _b, value); }
    public float A { get => _a; set => SetComponent(ref _a, value); }
    public LinearColor Value => new(R, G, B, A);
    public MediaColor? PreviewColor
    {
        get => MediaColor.FromArgb(ToByte(A), ToByte(PreviewColorSpace.LinearToSrgb(R)),
            ToByte(PreviewColorSpace.LinearToSrgb(G)), ToByte(PreviewColorSpace.LinearToSrgb(B)));
        set
        {
            if (value is not { } color)
            {
                return;
            }
            bool changed = false;
            changed |= SetProperty(ref _r, PreviewColorSpace.SrgbToLinear(color.R / 255f), nameof(R));
            changed |= SetProperty(ref _g, PreviewColorSpace.SrgbToLinear(color.G / 255f), nameof(G));
            changed |= SetProperty(ref _b, PreviewColorSpace.SrgbToLinear(color.B / 255f), nameof(B));
            changed |= SetProperty(ref _a, color.A / 255f, nameof(A));
            if (changed)
            {
                Changed();
            }
        }
    }

    public MorphColorOverrideItem(string name, LinearColor value, Action changed)
    {
        _name = name;
        _r = value.R;
        _g = value.G;
        _b = value.B;
        _a = value.A;
        Changed = changed;
    }

    private void SetComponent(ref float field, float value)
    {
        if (float.IsFinite(value) && SetProperty(ref field, value))
        {
            OnPropertyChanged(nameof(PreviewColor));
            Changed();
        }
    }
    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}

public sealed class MorphTextureOverrideItem : NotifyPropertyChangedBase
{
    private readonly Action Changed;
    private string _name;
    private int _entryIndex;
    private string _resolvedPath;
    public string Name { get => _name; set { if (SetProperty(ref _name, value ?? string.Empty)) Changed(); } }
    public int EntryIndex { get => _entryIndex; set { if (SetProperty(ref _entryIndex, value)) Changed(); } }
    public string ResolvedPath { get => _resolvedPath; internal set => SetProperty(ref _resolvedPath, value); }

    public MorphTextureOverrideItem(string name, int entryIndex, Action changed)
    {
        _name = name;
        _entryIndex = entryIndex;
        Changed = changed;
    }
}
