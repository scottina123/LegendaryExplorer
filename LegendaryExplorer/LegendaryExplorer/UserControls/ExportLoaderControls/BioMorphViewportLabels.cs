using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.Direct3D11;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public sealed class MorphViewportRegion
{
    public string Name { get; init; }
    public string Surface => Hair ? "Hair" : "Head";
    public string Label => $"{Number}. {Name}";
    public string Detail => $"{Surface} · {Name}";
    public int Number { get; init; }
    public SolidColorBrush Brush { get; init; }
    internal bool Hair { get; init; }
    internal float[] Weights { get; init; }
    internal int Anchor { get; init; }
    internal string MaterialKey { get; init; }
    internal MaterialRenderProxy Material { get; set; }
    internal MorphViewportPickMode Mode { get; init; }
}

public partial class MeshRenderer
{
    private bool showMorphRegionLabels = true;
    private object morphRegionHeadSource, morphRegionHairSource;
    private int morphRegionLod = -1;
    private string morphRegionFeatureNames;
    private bool morphRegionsDirty = true;
    private readonly Dictionary<(MorphViewportPickMode Mode, bool Hair, string Name), Color> morphRegionColors = [];
    private readonly List<MorphRegionSurface> morphRegionSurfaces = [];
    private GenericEffect<MeshRenderContext.WorldConstants, WorldVertex> morphRegionEffect;
    private DepthStencilState morphRegionDepthState;
    private RasterizerState morphRegionRasterizer;

    public ObservableCollectionExtended<MorphViewportRegion> MorphViewportRegionsList { get; } = [];
    public bool ShowMorphSelectedRegionLabel => ShowMorphRegionLabels && FindSelectedMorphRegion() == null;

    public bool ShowMorphRegionLabels
    {
        get => showMorphRegionLabels;
        set
        {
            if (!SetProperty(ref showMorphRegionLabels, value)) return;
            OnPropertyChanged(nameof(ShowMorphSelectedRegionLabel));
            UpdateMorphRegionCallouts();
        }
    }

    private sealed class MorphRegionSurface : IDisposable
    {
        internal bool Hair;
        internal int Lod;
        internal Func<int, Vector3> Position;
        internal Func<MorphViewportRegion, MorphViewportHit> Hit;
        internal Mesh<WorldVertex> Overlay;
        internal WorldVertex[] Vertices;
        internal MorphViewportRegion[] Regions;
        internal Vector4[] OverviewColors;
        internal int[] TriangleOwners;
        internal int SourceVertexCount;
        internal List<Triangle> SourceTriangles;
        internal string[] MaterialKeys;
        public void Dispose() => Overlay?.Dispose();
    }

    private void InvalidateMorphRegions()
    {
        morphRegionsDirty = true;
        ResetMorphRegionCallouts();
    }

    private MorphViewportRegion FindSelectedMorphRegion() => MorphViewportRegionsList.FirstOrDefault(region =>
        region.Mode == MorphViewportPickMode && SelectedMorphViewportMatch?.Name.Equals(region.Name, StringComparison.OrdinalIgnoreCase) == true
        && morphViewportHit?.Hair == region.Hair
        && (region.MaterialKey == null || morphViewportHit?.MaterialName == region.MaterialKey));

    private void UpdateMorphRegionLabels()
    {
        if (!ShowMorphEditorPanel || !HasMorphEditorData || MeshContext?.Device == null) return;
        object head = RenderGameShader && GameShaderPreview != null ? GameShaderPreview : LEXPreview;
        object hair = HideMorphHair ? null : RenderGameShader && MorphHairGameShaderPreview != null
            ? MorphHairGameShaderPreview : MorphHairLEXPreview;
        string featureNames = string.Join("\n", MorphFeatureItems.Select(item => item.Name)) + "\0"
            + string.Join("\n", MorphSkeletonItems.Select(item => item.Name));
        if (!morphRegionsDirty && ReferenceEquals(head, morphRegionHeadSource)
            && ReferenceEquals(hair, morphRegionHairSource) && morphRegionLod == CurrentLOD && morphRegionFeatureNames == featureNames) return;
        ClearMorphRegionSurfaces();
        morphRegionsDirty = false;
        morphRegionHeadSource = head;
        morphRegionHairSource = hair;
        morphRegionLod = CurrentLOD;
        morphRegionFeatureNames = featureNames;
        AddSurface(head, false);
        AddSurface(hair, true);
        UpdateMorphEditorRegionAccents();
        OnPropertyChanged(nameof(ShowMorphSelectedRegionLabel));

        void AddSurface(object source, bool isHair)
        {
            if (source is ModelPreview<WorldVertex> solid) BuildMorphRegionSurface(solid, isHair);
            else if (source is ModelPreview<LEVertex> shader) BuildMorphRegionSurface(shader, isHair);
        }
    }

    private void BuildMorphRegionSurface<T>(ModelPreview<T> preview, bool hair) where T : IVertexBase
    {
        if (preview.LODs.Count == 0 || CurrentLOD < 0) return;
        int lodIndex = hair ? Math.Min(CurrentLOD, preview.LODs.Count - 1) : CurrentLOD;
        if (lodIndex >= preview.LODs.Count) return;
        var lod = preview.LODs[lodIndex];
        int count = lod.Mesh.Vertices.Count;
        if (count == 0 || lod.Mesh.Triangles.Count == 0) return;
        var regionWeights = new List<(string Name, float[] Weights, string MaterialKey)>();
        var materialKeys = MorphViewportPickMode == MorphViewportPickMode.Materials ? new string[lod.Mesh.Triangles.Count] : null;
        MeshBone[] skeleton = hair ? MorphHairBindSkeleton : MorphBindSkeleton;
        var skin = (hair ? MorphHairSkinningInfluences : MorphSkinningInfluences).ElementAtOrDefault(lodIndex) ?? [];
        var directWeights = new Dictionary<int, float>[count];
        for (int vertex = 0; vertex < count; vertex++)
        {
            var weights = directWeights[vertex] = [];
            if (vertex >= skin.Length) continue;
            var value = skin[vertex];
            Add(value.Bone0, value.Weight0); Add(value.Bone1, value.Weight1);
            Add(value.Bone2, value.Weight2); Add(value.Bone3, value.Weight3);
            void Add(int bone, float weight)
            {
                if (bone >= 0 && bone < skeleton.Length && float.IsFinite(weight) && weight > 0)
                    weights[bone] = weights.GetValueOrDefault(bone) + weight;
            }
        }

        if (MorphViewportPickMode == MorphViewportPickMode.Features && !hair)
        {
            IReadOnlyDictionary<string, float>[] inherited = directWeights
                .Select(weights => (IReadOnlyDictionary<string, float>)MorphViewportPicking.IncludeParentWeights(skeleton, weights)).ToArray();
            var editedNames = MorphFeatureItems.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, target) in MorphTargets.Where(pair => editedNames.Contains(pair.Key)).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                regionWeights.Add((name, MorphViewportRegions.FeatureWeights(count,
                    target.Lods.ElementAtOrDefault(lodIndex)?.Vertices, target.BoneOffsets, inherited), null));
        }
        else if (MorphViewportPickMode == MorphViewportPickMode.Skeleton)
        {
            var editedBones = MorphSkeletonItems.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (int bone in directWeights.SelectMany(weights => weights.Keys).Distinct()
                         .Where(bone => editedBones.Contains(skeleton[bone].Name.Instanced)).OrderBy(bone => skeleton[bone].Name.Instanced))
                regionWeights.Add((skeleton[bone].Name.Instanced, directWeights.Select(weights => weights.GetValueOrDefault(bone)).ToArray(), null));
        }
        else if (MorphViewportPickMode == MorphViewportPickMode.Materials)
        {
            foreach (var group in lod.Sections.GroupBy(section => section.MaterialName).OrderBy(group => group.Key))
            {
                var weights = new float[count];
                foreach (var section in group)
                {
                    int end = Math.Min(lod.Mesh.Triangles.Count, (int)(section.StartIndex / 3 + section.TriangleCount));
                    for (int index = (int)section.StartIndex / 3; index < end; index++)
                    {
                        var triangle = lod.Mesh.Triangles[index];
                        materialKeys[index] = group.Key;
                        if (triangle.Vertex1 < count) weights[triangle.Vertex1] = 1;
                        if (triangle.Vertex2 < count) weights[triangle.Vertex2] = 1;
                        if (triangle.Vertex3 < count) weights[triangle.Vertex3] = 1;
                    }
                }
                preview.Materials.TryGetValue(group.Key, out var material);
                regionWeights.Add((material?.Material?.Export?.ObjectName.Instanced ?? group.Key, weights, group.Key));
            }
        }

        var regions = new List<MorphViewportRegion>();
        foreach (var (name, weights, materialKey) in regionWeights)
        {
            float maximum = weights.Max();
            if (maximum <= 0.00001f) continue;
            var key = (MorphViewportPickMode, hair, materialKey ?? name);
            if (!morphRegionColors.TryGetValue(key, out var color))
            {
                color = MorphViewportRegions.RegionColor(morphRegionColors.Keys.Count(item => item.Mode == MorphViewportPickMode));
                morphRegionColors.Add(key, color);
            }
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var region = new MorphViewportRegion
            {
                Name = name, Hair = hair, Weights = weights, Anchor = Array.IndexOf(weights, maximum), Mode = MorphViewportPickMode,
                Number = MorphViewportRegionsList.Count + 1, Brush = brush, MaterialKey = materialKey
            };
            regions.Add(region);
            MorphViewportRegionsList.Add(region);
        }
        if (regions.Count == 0) return;
        // Separate triangle corners keep neighboring region colors distinct and avoid blending
        // them into colors that have no matching label. Material boundaries use exact sections.
        var sourceIndices = lod.Mesh.Triangles.SelectMany(triangle => new[] { (int)triangle.Vertex1, (int)triangle.Vertex2, (int)triangle.Vertex3 }).ToArray();
        var surface = new MorphRegionSurface
        {
            Hair = hair, Lod = lodIndex, Position = index => lod.Mesh.Vertices[sourceIndices[index]].Position,
            Hit = CreateHit, Regions = regions.ToArray(), SourceVertexCount = count,
            SourceTriangles = lod.Mesh.Triangles, MaterialKeys = materialKeys, TriangleOwners = new int[lod.Mesh.Triangles.Count]
        };
        surface.OverviewColors = GetMorphSurfaceColors(surface);
        surface.Vertices = new WorldVertex[sourceIndices.Length];
        for (int index = 0; index < sourceIndices.Length; index++)
        {
            var color = surface.OverviewColors[index];
            surface.Vertices[index] = new WorldVertex(surface.Position(index), new Vector3(color.X, color.Y, color.Z), new Vector2(color.W, 0));
        }
        surface.Overlay = new Mesh<WorldVertex>(MeshContext.Device,
            Enumerable.Range(0, lod.Mesh.Triangles.Count).Select(index => new Triangle((uint)(index * 3), (uint)(index * 3 + 1), (uint)(index * 3 + 2))).ToList(),
            surface.Vertices.ToList());
        morphRegionSurfaces.Add(surface);

        MorphViewportHit CreateHit(MorphViewportRegion region)
        {
            int vertex = region.Anchor;
            foreach (var section in lod.Sections)
            {
                if (region.MaterialKey != null && region.MaterialKey != section.MaterialName) continue;
                int end = Math.Min(lod.Mesh.Triangles.Count, (int)(section.StartIndex / 3 + section.TriangleCount));
                for (int index = (int)section.StartIndex / 3; index < end; index++)
                {
                    var triangle = lod.Mesh.Triangles[index];
                    if (triangle.Vertex1 != vertex && triangle.Vertex2 != vertex && triangle.Vertex3 != vertex) continue;
                    preview.Materials.TryGetValue(section.MaterialName, out var material);
                    return new MorphViewportHit(hair, lodIndex, vertex, vertex, vertex, Vector3.UnitX, 0,
                        section.MaterialName, material?.Material);
                }
            }
            return new MorphViewportHit(hair, lodIndex, vertex, vertex, vertex, Vector3.UnitX, 0, "", null);
        }
    }

    private static Vector4[] GetMorphSurfaceColors(MorphRegionSurface surface)
    {
        if (surface.MaterialKeys == null)
            return MorphViewportRegions.SurfaceColors(surface.SourceVertexCount,
                surface.Regions.Select(region => region.Weights).ToArray(), surface.Regions.Select(region => region.Brush.Color).ToArray(),
                triangles: surface.SourceTriangles, owners: surface.TriangleOwners);
        var colors = new Vector4[surface.SourceTriangles.Count * 3];
        Array.Fill(surface.TriangleOwners, -1);
        var byMaterial = surface.Regions.ToDictionary(region => region.MaterialKey);
        for (int triangle = 0; triangle < surface.MaterialKeys.Length; triangle++)
        {
            if (surface.MaterialKeys[triangle] is not { } key || !byMaterial.TryGetValue(key, out var region)) continue;
            surface.TriangleOwners[triangle] = Array.IndexOf(surface.Regions, region);
            var color = region.Brush.Color;
            var tint = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 0.52f);
            colors[triangle * 3] = colors[triangle * 3 + 1] = colors[triangle * 3 + 2] = tint;
        }
        return colors;
    }

    private void MorphRegionLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MorphViewportRegion region }) return;
        var surface = morphRegionSurfaces.FirstOrDefault(item => item.Regions.Contains(region));
        if (surface == null) return;
        try
        {
            PauseMorphFaceFx();
            morphViewportHit = surface.Hit(region);
            BuildMorphViewportMatches(region);
            OnPropertyChanged(nameof(HasMorphViewportSelection));
        }
        catch (Exception ex)
        {
            ClearMorphViewportSelection();
            MorphViewportSelectionDetail = $"Could not select this region: {ex.Message}";
        }
    }

    // WorldVertex's normal/UV fields carry RGB/opacity for this unlit diagnostic effect. The
    // positions come from the live skinned mesh, so tint follows edits, camera movement and FaceFX.
    internal const string MorphRegionShader = """
        cbuffer World : register(b0) { float4x4 Projection; float4x4 View; float4x4 Model; };
        struct Vertex { float3 Position : POSITION; float3 Color : NORMAL; float2 Alpha : TEXCOORD; };
        struct Pixel { float4 Position : SV_POSITION; float4 Color : COLOR; };
        Pixel VSMain(Vertex input) {
            Pixel output;
            output.Position = mul(mul(float4(input.Position, 1), View), Projection);
            output.Color = float4(input.Color, input.Alpha.x);
            return output;
        }
        float4 PSMain(Pixel input) : SV_TARGET { return input.Color; }
        """;

    private void RenderMorphRegions()
    {
        if (!ShowMorphEditorPanel || !HasMorphEditorData || !ShowMorphRegionLabels || morphRegionSurfaces.Count == 0) return;
        var context = MeshContext.ImmediateContext;
        if (morphRegionEffect == null)
        {
            morphRegionEffect = new GenericEffect<MeshRenderContext.WorldConstants, WorldVertex>(MeshContext.Device, MorphRegionShader);
            morphRegionDepthState = new DepthStencilState(MeshContext.Device, new DepthStencilStateDescription
            {
                IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.LessEqual
            });
            morphRegionRasterizer = new RasterizerState(MeshContext.Device, new RasterizerStateDescription
            {
                FillMode = FillMode.Solid, CullMode = CullMode.None, IsDepthClipEnabled = true,
                DepthBias = -1, SlopeScaledDepthBias = -1
            });
        }
        using var previousDepth = context.OutputMerger.GetDepthStencilState(out int stencilReference);
        using var previousRasterizer = context.Rasterizer.State;
        try
        {
            context.OutputMerger.SetDepthStencilState(morphRegionDepthState);
            context.Rasterizer.State = morphRegionRasterizer;
            morphRegionEffect.PrepDraw(context, MeshContext.AlphaBlendState);
            var constants = new MeshRenderContext.WorldConstants(Matrix4x4.Transpose(MeshContext.Camera.ProjectionMatrix),
                Matrix4x4.Transpose(MeshContext.Camera.ViewMatrix), Matrix4x4.Identity, MeshContext.CurrentTextureViewFlags);
            foreach (var surface in morphRegionSurfaces)
            {
                for (int vertex = 0; vertex < surface.Vertices.Length; vertex++)
                {
                    var color = surface.OverviewColors[vertex];
                    surface.Vertices[vertex] = new WorldVertex(surface.Position(vertex), new Vector3(color.X, color.Y, color.Z), new Vector2(color.W, 0));
                }
                context.UpdateSubresource(surface.Vertices, surface.Overlay.VertexBuffer);
                morphRegionEffect.RenderObject(context, constants, surface.Overlay);
            }
        }
        finally
        {
            context.OutputMerger.SetDepthStencilState(previousDepth, stencilReference);
            context.Rasterizer.State = previousRasterizer;
        }
    }

    private void ClearMorphRegionSurfaces()
    {
        foreach (var surface in morphRegionSurfaces) surface.Dispose();
        morphRegionSurfaces.Clear();
        MorphViewportRegionsList.ClearEx();
        ResetMorphRegionCallouts();
    }

    private void UnloadMorphRegionLabels()
    {
        ClearMorphRegionSurfaces();
        morphRegionEffect?.Dispose(); morphRegionEffect = null;
        morphRegionDepthState?.Dispose(); morphRegionDepthState = null;
        morphRegionRasterizer?.Dispose(); morphRegionRasterizer = null;
        morphRegionHeadSource = morphRegionHairSource = null;
        morphRegionColors.Clear();
        InvalidateMorphRegions();
        OnPropertyChanged(nameof(ShowMorphSelectedRegionLabel));
    }
}
