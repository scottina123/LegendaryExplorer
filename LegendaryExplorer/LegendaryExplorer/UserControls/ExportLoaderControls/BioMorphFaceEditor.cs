using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
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
using MediaColor = System.Windows.Media.Color;
using BinaryMorphFace = LegendaryExplorerCore.Unreal.BinaryConverters.BioMorphFace;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

/// <summary>
/// LE3-only BioMorphFace editor. It inherits the mesh viewport so the preview uses the exact same
/// LE shader path as the regular mesh renderer.
/// </summary>
public sealed class BioMorphFaceEditor : MeshRenderer
{
    public BioMorphFaceEditor() : base(true)
    {
    }

    public override bool CanParse(ExportEntry exportEntry) =>
        exportEntry is { Game: MEGame.LE3, ClassName: "BioMorphFace", IsDefaultObject: false };

    public override void PopOut()
    {
        if (CurrentLoadedExport is null)
        {
            return;
        }

        var window = new ExportLoaderHostedWindow(new BioMorphFaceEditor(), CurrentLoadedExport)
        {
            Title = $"Morph Editor - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath} - {CurrentLoadedExport.FileRef.FilePath}"
        };
        window.Show();
    }
}

public partial class MeshRenderer
{
    private sealed record MorphFeatureSnapshot(string Name, float Value);
    private sealed record MorphTargetSnapshot(MorphTarget.MorphLODModel[] Lods, MorphTarget.BoneOffset[] BoneOffsets);
    private readonly record struct MorphSkinInfluences(
        int Bone0, int Bone1, int Bone2, int Bone3,
        float Weight0, float Weight1, float Weight2, float Weight3);

    private ExportEntry MorphBaseHeadExport;
    private SkeletalMesh MorphPreviewSkeletalMesh;
    private MeshBone[] MorphBindSkeleton = [];
    private MorphSkinInfluences[][] MorphSkinningInfluences = [];
    private Vector3[][] StoredMorphLods = [];
    private Vector3[][] WorkingMorphLods = [];
    private Vector3[][] WorkingMorphNormalDeltas = [];
    private List<MorphFeatureSnapshot> OriginalMorphFeatures = [];
    private Dictionary<string, MorphTargetSnapshot> MorphTargets = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Vector3> BaseSkeletonPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> RemovedMorphBones = new(StringComparer.OrdinalIgnoreCase);
    private bool SuppressMorphEditorChanges;
    private string PendingMorphTargetStatus;

    public ObservableCollectionExtended<MorphFeatureEditorItem> MorphFeatureItems { get; } = [];
    public IEnumerable<MorphFeatureEditorItem> MatchedMorphFeatureItems =>
        MorphFeatureItems.Where(feature => feature.HasMorphTarget && MatchesMorphEditorSearch(feature.Name)).ToArray();
    public IEnumerable<MorphFeatureEditorItem> UnmatchedMorphFeatureItems =>
        MorphFeatureItems.Where(feature => !feature.HasMorphTarget && MatchesMorphEditorSearch(feature.Name)).ToArray();
    public int UnmatchedMorphFeatureCount => UnmatchedMorphFeatureItems.Count();
    public bool HasUnmatchedMorphFeatures => UnmatchedMorphFeatureCount > 0;
    public ObservableCollectionExtended<MorphBoneEditorItem> MorphSkeletonItems { get; } = [];
    public IEnumerable<MorphBoneEditorItem> FilteredMorphSkeletonItems =>
        MorphSkeletonItems.Where(bone => MatchesMorphEditorSearch(bone.Name)).ToArray();
    public ObservableCollectionExtended<MorphScalarOverrideItem> MorphScalarOverrides { get; } = [];
    public IEnumerable<MorphScalarOverrideItem> FilteredMorphScalarOverrides =>
        MorphScalarOverrides.Where(scalar => MatchesMorphEditorSearch(scalar.Name)).ToArray();
    public ObservableCollectionExtended<MorphColorOverrideItem> MorphColorOverrides { get; } = [];
    public IEnumerable<MorphColorOverrideItem> FilteredMorphColorOverrides =>
        MorphColorOverrides.Where(color => MatchesMorphEditorSearch(color.Name)).ToArray();
    public ObservableCollectionExtended<MorphTextureOverrideItem> MorphTextureOverrides { get; } = [];
    public IEnumerable<MorphTextureOverrideItem> FilteredMorphTextureOverrides => MorphTextureOverrides
        .Where(texture => MatchesMorphEditorSearch(texture.Name, texture.ResolvedPath, texture.EntryIndex.ToString()))
        .ToArray();

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

    private string _morphBaseHeadPath;
    public string MorphBaseHeadPath
    {
        get => _morphBaseHeadPath;
        private set => SetProperty(ref _morphBaseHeadPath, value);
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
        private set => SetProperty(ref _hasMorphEditorData, value);
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
            MorphBaseHeadPath = $"{baseHead.InstancedFullPath} ({Path.GetFileName(baseHead.FileRef.FilePath)})";

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

    private PreloadedModelData CreateMorphPreloadedModelData(PackageCache assetCache, ExportEntry morphSource, ExportEntry baseHead)
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

        var lodMaterialMaps = new List<int[]>();
        if (baseHead.GetProperty<ArrayProperty<StructProperty>>("LODInfo", assetCache) is { } lodInfo)
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
            lodMaterialMaps = lodMaterialMaps
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
        string cookedPath = MEDirectories.GetCookedPath(MEGame.LE3);
        if (!string.IsNullOrWhiteSpace(prefix) && Directory.Exists(cookedPath))
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(cookedPath, $"BIOG_{prefix}_*PROMorph*.pcc", SearchOption.TopDirectoryOnly))
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

    private void UpdateMorphGeometryPreview()
    {
        ApplyWorkingLodsToSkeletalMesh(MorphPreviewSkeletalMesh);
        Matrix4x4[] skinningMatrices = ComputeMorphSkinningMatrices();
        for (int lodIndex = 0; lodIndex < WorkingMorphLods.Length; lodIndex++)
        {
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
                    mesh.Vertices[i] = mesh.Vertices[i].WithPositionAndNormal(
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
    }

    private Matrix4x4[] ComputeMorphSkinningMatrices()
    {
        MeshBone[] editedSkeleton = MorphPreviewSkeletalMesh?.RefSkeleton;
        int boneCount = Math.Min(MorphBindSkeleton.Length, editedSkeleton?.Length ?? 0);
        if (boneCount == 0)
        {
            return [];
        }

        var bindComponentSpace = new Matrix4x4[boneCount];
        var editedComponentSpace = new Matrix4x4[boneCount];
        var skinningMatrices = new Matrix4x4[boneCount];
        for (int boneIndex = 0; boneIndex < boneCount; boneIndex++)
        {
            MeshBone bindBone = MorphBindSkeleton[boneIndex];
            MeshBone editedBone = editedSkeleton[boneIndex];
            Matrix4x4 bindLocal = Matrix4x4.CreateFromQuaternion(bindBone.Orientation)
                                  * Matrix4x4.CreateTranslation(bindBone.Position);
            Matrix4x4 editedLocal = Matrix4x4.CreateFromQuaternion(editedBone.Orientation)
                                    * Matrix4x4.CreateTranslation(editedBone.Position);
            if (bindBone.ParentIndex >= 0 && bindBone.ParentIndex < boneIndex)
            {
                bindComponentSpace[boneIndex] = bindLocal * bindComponentSpace[bindBone.ParentIndex];
            }
            else
            {
                bindComponentSpace[boneIndex] = bindLocal;
            }
            if (editedBone.ParentIndex >= 0 && editedBone.ParentIndex < boneIndex)
            {
                editedComponentSpace[boneIndex] = editedLocal * editedComponentSpace[editedBone.ParentIndex];
            }
            else
            {
                editedComponentSpace[boneIndex] = editedLocal;
            }
            skinningMatrices[boneIndex] = Matrix4x4.Invert(bindComponentSpace[boneIndex], out Matrix4x4 inverseBind)
                ? inverseBind * editedComponentSpace[boneIndex]
                : Matrix4x4.Identity;
        }
        return skinningMatrices;
    }

    private Vector3 SkinMorphPosition(Vector3 position, int lodIndex, int vertexIndex, Matrix4x4[] skinningMatrices)
    {
        if (skinningMatrices.Length == 0
            || lodIndex >= MorphSkinningInfluences.Length
            || vertexIndex >= MorphSkinningInfluences[lodIndex].Length)
        {
            return position;
        }
        MorphSkinInfluences influence = MorphSkinningInfluences[lodIndex][vertexIndex];
        Matrix4x4 blended = GetSkinningMatrix(skinningMatrices, influence.Bone0) * influence.Weight0;
        if (influence.Weight1 > 0) blended += GetSkinningMatrix(skinningMatrices, influence.Bone1) * influence.Weight1;
        if (influence.Weight2 > 0) blended += GetSkinningMatrix(skinningMatrices, influence.Bone2) * influence.Weight2;
        if (influence.Weight3 > 0) blended += GetSkinningMatrix(skinningMatrices, influence.Bone3) * influence.Weight3;
        return Vector3.Transform(position, blended);
    }

    private static Matrix4x4 GetSkinningMatrix(Matrix4x4[] matrices, int boneIndex) =>
        boneIndex >= 0 && boneIndex < matrices.Length ? matrices[boneIndex] : Matrix4x4.Identity;

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
        if (MeshContext.IsReady)
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
        ApplyMorphMaterialOverridePreview();
        MarkMorphChanged();
    }

    private void ApplyMorphMaterialOverridePreview()
    {
        if (GameShaderPreview is null || CurrentLoadedExport is null)
        {
            return;
        }
        List<MaterialRenderProxy> materials = GameShaderPreview.Materials.Values
            .Select(value => value.Material)
            .OfType<MaterialRenderProxy>()
            .Distinct()
            .ToList();
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

        using var cache = new PackageCache();
        foreach (MorphTextureOverrideItem texture in MorphTextureOverrides)
        {
            ExportEntry textureExport = CurrentLoadedExport.FileRef.GetEntry(texture.EntryIndex) switch
            {
                ExportEntry export when export.IsTexture() => export,
                ImportEntry import => EntryImporter.ResolveImport(import, cache),
                _ => null
            };
            if (textureExport is not null && !textureExport.IsTexture())
            {
                textureExport = null;
            }
            PreviewTextureCache.TextureEntry cachedTexture = textureExport is not null
                ? MeshContext.TextureCache.LoadTexture(textureExport)
                : null;
            texture.ResolvedPath = textureExport?.InstancedFullPath ?? (texture.EntryIndex == 0 ? "None" : "Entry is not a resolvable texture");
            foreach (MaterialRenderProxy material in materials)
            {
                material.SetTextureParameter(texture.Name, textureExport?.InstancedFullPath, cachedTexture);
            }
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
        if (CurrentLoadedExport is null || !HasMorphEditorData)
        {
            return;
        }
        try
        {
            WriteMorphEditorValues(CurrentLoadedExport);
            HasUnsavedMorphChanges = false;
            MorphEditorStatus = $"Overwrote {CurrentLoadedExport.InstancedFullPath}.";
        }
        catch (Exception exception)
        {
            new ExceptionHandlerDialog(exception).ShowDialog();
        }
    }

    private void SaveMorphAsNew_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentLoadedExport is null || !HasMorphEditorData)
        {
            return;
        }
        string suggestedName = $"{CurrentLoadedExport.ObjectName.Name}_Edited";
        string name = PromptDialog.Prompt(this, "Name the new BioMorphFace:", "Make new morph", suggestedName,
            selectText: true, validator: ValidateNewMorphName);
        if (name is null)
        {
            return;
        }
        try
        {
            ExportEntry clone = EntryCloner.CloneTree(CurrentLoadedExport);
            clone.ObjectName = new NameReference(name.Trim());
            EnsureNewMorphHasIndependentMaterialOverride(clone);
            WriteMorphEditorValues(clone);
            HasUnsavedMorphChanges = false;
            MorphEditorStatus = $"Created export {clone.UIndex}: {clone.InstancedFullPath}.";
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

    private void RemoveMorphFeature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MorphFeatureEditorItem item } && MorphFeatureItems.Remove(item))
        {
            RefreshMorphFeatureGroups();
            OnMorphFeatureChanged();
        }
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

    private void AddMorphScalar_Click(object sender, RoutedEventArgs e) => AddMaterialItem(
        "Scalar parameter name:", name => MorphScalarOverrides.Add(new MorphScalarOverrideItem(name, 0, OnMorphMaterialChanged)));

    private void AddMorphColor_Click(object sender, RoutedEventArgs e) => AddMaterialItem(
        "Color parameter name:", name => MorphColorOverrides.Add(new MorphColorOverrideItem(name, LinearColor.White, OnMorphMaterialChanged)));

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
            MorphPreviewSkeletalMesh = null;
            MorphBaseHeadPath = null;
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
