using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MaterialInstance = LegendaryExplorerCore.Unreal.Classes.MaterialInstanceConstant;
using Point = System.Windows.Point;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public sealed class MorphViewportMatch
{
    internal MorphViewportPickMode Mode { get; init; }
    internal string TargetName { get; init; }
    internal MorphFeatureEditorItem Feature { get; set; }
    internal MorphBoneEditorItem Bone { get; set; }
    internal MaterialRenderProxy Material { get; init; }
    internal Vector3 BonePosition { get; init; }
    internal float Strength { get; init; }
    public string Name => Feature?.Name ?? Bone?.Name ?? TargetName;
    public string Description { get; init; }
    public string Label => $"{Name} — {Description}";
}

public partial class MeshRenderer
{
    private sealed record MorphViewportHit(bool Hair, int Lod, int A, int B, int C, Vector3 Barycentric,
        float Distance, string MaterialName, MaterialInstance Material);

    private MorphViewportHit morphViewportHit;
    private MorphViewportPickMode morphViewportPickMode;
    private MorphViewportMatch selectedMorphViewportMatch;
    private bool filterMorphViewportSelection = true;

    public IReadOnlyList<MorphViewportPickMode> MorphViewportPickModes { get; } = Enum.GetValues<MorphViewportPickMode>();
    public ObservableCollectionExtended<MorphViewportMatch> MorphViewportMatches { get; } = [];
    public bool HasMorphViewportMatches => MorphViewportMatches.Count > 0;
    public bool HasMorphViewportSelection => morphViewportHit != null;
    public bool CanAddMorphViewportSelection => CanEditMorph && SelectedMorphViewportMatch is { } match
        && (match.Mode == MorphViewportPickMode.Features && !MorphFeatureItems.Any(item => item.Name == match.Name)
            || match.Mode == MorphViewportPickMode.Skeleton && !MorphSkeletonItems.Any(item => item.Name == match.Name));
    public string AddMorphViewportSelectionText => MorphViewportPickMode == MorphViewportPickMode.Skeleton
        ? "Add selected bone override" : "Add selected feature";
    public string MorphViewportPickHelp => MorphViewportPickMode switch
    {
        MorphViewportPickMode.Features => "Click a face region to find features that move it. Drag to orbit.",
        MorphViewportPickMode.Skeleton => "Click the head or hair to find bones that influence that surface.",
        _ => "Click a surface to select its material and show matching morph overrides."
    };

    private string morphViewportSelectionLabel = "No viewport selection";
    public string MorphViewportSelectionLabel
    {
        get => morphViewportSelectionLabel;
        private set => SetProperty(ref morphViewportSelectionLabel, value);
    }

    private string morphViewportSelectionDetail;
    public string MorphViewportSelectionDetail
    {
        get => morphViewportSelectionDetail;
        private set => SetProperty(ref morphViewportSelectionDetail, value);
    }

    public MorphViewportPickMode MorphViewportPickMode
    {
        get => morphViewportPickMode;
        set
        {
            if (!SetProperty(ref morphViewportPickMode, value)) return;
            OnPropertyChanged(nameof(MorphViewportPickHelp));
            OnPropertyChanged(nameof(AddMorphViewportSelectionText));
            ShowMorphViewportCategory();
            if (morphViewportHit != null) BuildMorphViewportMatches();
        }
    }

    public bool FilterMorphViewportSelection
    {
        get => filterMorphViewportSelection;
        set { if (SetProperty(ref filterMorphViewportSelection, value)) RefreshMorphEditorFilters(); }
    }

    public MorphViewportMatch SelectedMorphViewportMatch
    {
        get => selectedMorphViewportMatch;
        set
        {
            if (ReferenceEquals(selectedMorphViewportMatch, value)) return;
            if (selectedMorphViewportMatch?.Bone is { } oldBone) oldBone.IsViewportSelected = false;
            SetProperty(ref selectedMorphViewportMatch, value);
            if (value?.Bone is { } bone) bone.IsViewportSelected = true;
            if (value != null)
            {
                string category = value.Mode == MorphViewportPickMode.Features ? "Feature"
                    : value.Mode == MorphViewportPickMode.Skeleton ? "Bone" : "Material";
                MorphViewportSelectionLabel = $"{category}: {value.Name}";
                MorphViewportSelectionDetail = $"{(morphViewportHit?.Hair == true ? "Hair" : "Head")} · {value.Description}"
                    + (CanAddMorphViewportSelection ? " · Not overridden; add it to edit." : "")
                    + (value.Mode == MorphViewportPickMode.Materials ? " · Shared parameter names can affect other materials." : "");
                MorphEditorSearchText = null;
                ShowMorphViewportCategory();
            }
            RefreshMorphEditorFilters();
            OnPropertyChanged(nameof(CanAddMorphViewportSelection));
            UpdateMorphViewportMarker();
        }
    }

    private void ShowMorphViewportCategory()
    {
        if (MorphEditorTabs == null) return;
        MorphEditorTabs.SelectedItem = MorphViewportPickMode switch
        {
            MorphViewportPickMode.Features => MorphFeaturesTab,
            MorphViewportPickMode.Skeleton => MorphSkeletonTab,
            _ => MorphMaterialsTab
        };
    }

    private void MorphEditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MorphEditorTabs)) return;
        if (MorphEditorTabs.SelectedItem == MorphFeaturesTab) MorphViewportPickMode = MorphViewportPickMode.Features;
        else if (MorphEditorTabs.SelectedItem == MorphSkeletonTab) MorphViewportPickMode = MorphViewportPickMode.Skeleton;
        else if (MorphEditorTabs.SelectedItem == MorphMaterialsTab) MorphViewportPickMode = MorphViewportPickMode.Materials;
    }

    private bool MatchesMorphViewportFeature(MorphFeatureEditorItem item) => !FilterMorphViewportSelection
        || SelectedMorphViewportMatch is not { Mode: MorphViewportPickMode.Features } match
        || (match.Feature != null ? ReferenceEquals(item, match.Feature) : item.Name.Equals(match.Name, StringComparison.OrdinalIgnoreCase));

    private bool MatchesMorphViewportBone(MorphBoneEditorItem item) => !FilterMorphViewportSelection
        || SelectedMorphViewportMatch is not { Mode: MorphViewportPickMode.Skeleton } match
        || (match.Bone != null ? ReferenceEquals(item, match.Bone) : item.Name.Equals(match.Name, StringComparison.OrdinalIgnoreCase));

    private bool MatchesMorphViewportMaterial(string name, int parameterType)
    {
        if (!FilterMorphViewportSelection || SelectedMorphViewportMatch is not { Mode: MorphViewportPickMode.Materials } match)
            return true;
        return match.Material != null && (parameterType switch
        {
            0 => match.Material.DefinesScalarParameter(name),
            1 => match.Material.DefinesVectorParameter(name),
            _ => match.Material.DefinesTextureParameter(name)
        });
    }

    private List<MaterialRenderProxy> GetMorphMaterialSelectionTargets() =>
        SelectedMorphViewportMatch is { Mode: MorphViewportPickMode.Materials, Material: { } material }
            ? [material] : GetMorphMaterialPreviewTargets();

    private void PickMorphViewport(Point screenPosition)
    {
        if (!ShowMorphEditorPanel || !HasMorphEditorData) return;
        try
        {
            MorphViewportHit hit = FindMorphViewportHit(screenPosition);
            ClearMorphViewportSelection();
            if (hit == null)
            {
                MorphViewportSelectionDetail = "No surface at this position. Click the visible head or hair.";
                return;
            }
            PauseMorphFaceFx();
            morphViewportHit = hit;
            BuildMorphViewportMatches();
            OnPropertyChanged(nameof(HasMorphViewportSelection));
            UpdateMorphViewportMarker();
        }
        catch (Exception ex)
        {
            ClearMorphViewportSelection();
            MorphViewportSelectionDetail = $"Could not select this region: {ex.Message}";
        }
    }

    private MorphViewportHit FindMorphViewportHit(Point screenPosition)
    {
        if (SceneViewer.ActualWidth <= 0 || SceneViewer.ActualHeight <= 0
            || !Matrix4x4.Invert(MeshContext.Camera.ViewMatrix * MeshContext.Camera.ProjectionMatrix, out var inverse)) return null;
        float x = (float)(2 * screenPosition.X / SceneViewer.ActualWidth - 1);
        float y = (float)(1 - 2 * screenPosition.Y / SceneViewer.ActualHeight);
        Vector4 near = Vector4.Transform(new Vector4(x, y, 0, 1), inverse);
        Vector4 far = Vector4.Transform(new Vector4(x, y, 1, 1), inverse);
        if (Math.Abs(near.W) < float.Epsilon || Math.Abs(far.W) < float.Epsilon) return null;
        Vector3 origin = new(near.X / near.W, near.Y / near.W, near.Z / near.W);
        Vector3 end = new(far.X / far.W, far.Y / far.W, far.Z / far.W);
        Vector3 direction = Vector3.Normalize(end - origin);
        MorphViewportHit nearest = null;
        if (RenderGameShader && GameShaderPreview != null) Scan(GameShaderPreview, false);
        else if (RenderSolid || RenderWireframe) Scan(LEXPreview, false);
        if (!HideMorphHair)
        {
            if (RenderGameShader && MorphHairGameShaderPreview != null) Scan(MorphHairGameShaderPreview, true);
            else if (RenderSolid || RenderWireframe) Scan(MorphHairLEXPreview, true);
        }
        return nearest;

        void Scan<T>(ModelPreview<T> preview, bool hair) where T : IVertexBase
        {
            if (preview?.LODs.Count is not > 0 || CurrentLOD < 0) return;
            int lodIndex = hair ? Math.Min(CurrentLOD, preview.LODs.Count - 1) : CurrentLOD;
            if (lodIndex >= preview.LODs.Count) return;
            var lod = preview.LODs[lodIndex];
            foreach (var section in lod.Sections)
            {
                int first = (int)section.StartIndex / 3;
                int last = Math.Min(lod.Mesh.Triangles.Count, first + (int)section.TriangleCount);
                for (int i = first; i < last; i++)
                {
                    Triangle triangle = lod.Mesh.Triangles[i];
                    int a = (int)triangle.Vertex1, b = (int)triangle.Vertex2, c = (int)triangle.Vertex3;
                    var vertices = lod.Mesh.Vertices;
                    if ((uint)a >= vertices.Count || (uint)b >= vertices.Count || (uint)c >= vertices.Count) continue;
                    if (!MorphViewportPicking.IntersectTriangle(origin, direction, vertices[a].Position, vertices[b].Position,
                            vertices[c].Position, out float distance, out var barycentric)
                        || nearest != null && distance >= nearest.Distance) continue;
                    preview.Materials.TryGetValue(section.MaterialName, out var material);
                    nearest = new MorphViewportHit(hair, lodIndex, a, b, c, barycentric, distance, section.MaterialName, material?.Material);
                }
            }
        }
    }

    private void BuildMorphViewportMatches()
    {
        SelectedMorphViewportMatch = null;
        MorphViewportMatches.ClearEx();
        if (morphViewportHit is not { } hit) return;
        MorphSkinInfluences[][] influences = hit.Hair ? MorphHairSkinningInfluences : MorphSkinningInfluences;
        MeshBone[] skeleton = hit.Hair ? MorphHairBindSkeleton : MorphBindSkeleton;
        var weights = MorphViewportPicking.BlendBoneWeights(GetInfluences(hit.A), GetInfluences(hit.B), GetInfluences(hit.C), hit.Barycentric);
        if (MorphViewportPickMode == MorphViewportPickMode.Features && !hit.Hair)
        {
            var inheritedWeights = MorphViewportPicking.IncludeParentWeights(skeleton, weights);
            foreach (var (name, target) in MorphTargets)
            {
                float strength = MorphViewportPicking.FeatureStrength(target.Lods.ElementAtOrDefault(hit.Lod)?.Vertices,
                    target.BoneOffsets, hit.A, hit.B, hit.C, hit.Barycentric, inheritedWeights);
                if (strength <= 0.00001f) continue;
                MorphViewportMatches.Add(new MorphViewportMatch
                {
                    Mode = MorphViewportPickMode.Features, TargetName = name, Strength = strength,
                    Feature = MorphFeatureItems.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)),
                    Description = $"{strength:F3} movement per unit"
                });
            }
        }
        else if (MorphViewportPickMode == MorphViewportPickMode.Skeleton)
        {
            foreach (var (index, weight) in weights.Where(pair => pair.Key < skeleton.Length && pair.Value > 0.0001f))
            {
                string name = skeleton[index].Name.Instanced;
                MorphViewportMatches.Add(new MorphViewportMatch
                {
                    Mode = MorphViewportPickMode.Skeleton, TargetName = name, Strength = weight, BonePosition = skeleton[index].Position,
                    Bone = MorphSkeletonItems.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)),
                    Description = $"{weight:P0} skin weight"
                });
            }
        }
        else if (MorphViewportPickMode == MorphViewportPickMode.Materials)
        {
            // Prefer the retained shader material even when the viewport currently uses the solid renderer.
            var preview = hit.Hair ? MorphHairGameShaderPreview : GameShaderPreview;
            MaterialRenderProxy material = hit.Material as MaterialRenderProxy;
            if (material == null && preview?.Materials.TryGetValue(hit.MaterialName, out var shaderMaterial) == true)
                material = shaderMaterial.Material as MaterialRenderProxy;
            if (material == null && hit.Material?.Export is { } export)
            {
                using var cache = new PackageCache();
                material = new MaterialRenderProxy(export, cache);
            }
            MorphViewportMatches.Add(new MorphViewportMatch
            {
                Mode = MorphViewportPickMode.Materials, TargetName = hit.Material?.Export?.ObjectName.Instanced ?? hit.MaterialName,
                Material = material, Description = hit.Material?.Export?.InstancedFullPath ?? "Surface material"
            });
        }
        MorphViewportMatches.ReplaceAll(MorphViewportMatches.OrderByDescending(item => item.Strength).ThenBy(item => item.Name).ToArray());
        OnPropertyChanged(nameof(HasMorphViewportMatches));
        SelectedMorphViewportMatch = MorphViewportMatches.FirstOrDefault();
        if (SelectedMorphViewportMatch == null)
        {
            MorphViewportSelectionLabel = $"{MorphViewportPickMode}: no match";
            MorphViewportSelectionDetail = hit.Hair && MorphViewportPickMode == MorphViewportPickMode.Features
                ? "Hair mesh selected. Face features belong to the head; select Skeleton or Materials for hair."
                : MorphViewportPickMode == MorphViewportPickMode.Features && MorphTargets.Count == 0
                    ? "Morph targets are still loading or are unavailable."
                    : "No matching controls influence the clicked surface.";
        }

        (int Bone, float Weight)[] GetInfluences(int vertex)
        {
            if (hit.Lod >= influences.Length || vertex >= influences[hit.Lod].Length) return [];
            var value = influences[hit.Lod][vertex];
            return [(value.Bone0, value.Weight0), (value.Bone1, value.Weight1), (value.Bone2, value.Weight2), (value.Bone3, value.Weight3)];
        }
    }

    private void AddMorphViewportSelection_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAddMorphViewportSelection || SelectedMorphViewportMatch is not { } match) return;
        if (match.Mode == MorphViewportPickMode.Features)
        {
            match.Feature = new MorphFeatureEditorItem(match.Name, 0, OnMorphFeatureChanged) { HasMorphTarget = true };
            MorphFeatureItems.Add(match.Feature);
            OnMorphFeatureChanged();
        }
        else
        {
            RemovedMorphBones.Remove(match.Name);
            match.Bone = new MorphBoneEditorItem(match.Name, match.BonePosition, OnMorphBoneChanged) { IsViewportSelected = true };
            MorphSkeletonItems.Add(match.Bone);
            OnMorphBoneChanged();
        }
        SelectedMorphViewportMatch = null;
        SelectedMorphViewportMatch = match;
    }

    private void ClearMorphViewportSelection_Click(object sender, RoutedEventArgs e) => ClearMorphViewportSelection();

    private void ClearMorphViewportSelection()
    {
        morphViewportHit = null;
        SelectedMorphViewportMatch = null;
        MorphViewportMatches.ClearEx();
        MorphViewportSelectionLabel = "No viewport selection";
        MorphViewportSelectionDetail = null;
        OnPropertyChanged(nameof(HasMorphViewportSelection));
        OnPropertyChanged(nameof(HasMorphViewportMatches));
        UpdateMorphViewportMarker();
    }

    private void UpdateMorphViewportMarker()
    {
        if (MorphViewportMarker == null) return;
        MorphViewportMarker.Visibility = Visibility.Collapsed;
        if (morphViewportHit is not { } hit || !ShowMorphEditorPanel || SceneViewer.ActualWidth <= 0
            || SceneViewer.ActualHeight <= 0 || hit.Hair && HideMorphHair) return;
        var preview = hit.Hair ? MorphHairLEXPreview : LEXPreview;
        if (preview == null || hit.Lod >= preview.LODs.Count) return;
        var vertices = preview.LODs[hit.Lod].Mesh.Vertices;
        if (hit.A >= vertices.Count || hit.B >= vertices.Count || hit.C >= vertices.Count) return;
        Vector3 position = vertices[hit.A].Position * hit.Barycentric.X + vertices[hit.B].Position * hit.Barycentric.Y
                           + vertices[hit.C].Position * hit.Barycentric.Z;
        Vector4 clip = Vector4.Transform(new Vector4(position, 1), MeshContext.Camera.ViewMatrix * MeshContext.Camera.ProjectionMatrix);
        if (clip.W <= 0 || clip.Z < 0 || clip.Z > clip.W || Math.Abs(clip.X) > clip.W || Math.Abs(clip.Y) > clip.W) return;
        double x = (clip.X / clip.W + 1) * SceneViewer.ActualWidth / 2;
        double y = (1 - clip.Y / clip.W) * SceneViewer.ActualHeight / 2;
        Canvas.SetLeft(MorphViewportMarkerDot, x - 7);
        Canvas.SetTop(MorphViewportMarkerDot, y - 7);
        Canvas.SetLeft(MorphViewportMarkerLabel, Math.Clamp(x + 12, 0, Math.Max(0, SceneViewer.ActualWidth - MorphViewportMarkerLabel.ActualWidth)));
        Canvas.SetTop(MorphViewportMarkerLabel, Math.Clamp(y + 8, 0, Math.Max(0, SceneViewer.ActualHeight - MorphViewportMarkerLabel.ActualHeight)));
        MorphViewportMarker.Visibility = Visibility.Visible;
    }
}
