using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using SharpDX.Direct3D11;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// The Level Editor's lens flares. A LensFlare template's elements are prepared once (one material per element material, with the
/// FLensFlareVertexFactory shaders), and each LensFlareSource's <see cref="LensFlareComponentProxy"/> draws through the context's translucent queue
/// and game-shader path (<see cref="VfxGameShaderRenderer.RenderNativeMaterial{TVertex}"/>)
/// </summary>
internal sealed class LevelLensFlareRenderer(LevelEditorRenderContext context) : IDisposable
{
    private const string VertexFactory = "FLensFlareVertexFactory";
    private readonly Dictionary<ExportEntry, List<PreparedFlare>> templates = [];

    internal sealed class MaterialResources : IDisposable
    {
        public MaterialRenderProxy Material;
        public Mesh<LensFlareVertex> Mesh;
        public RenderTargetBlendDescription Blend;
        public void Dispose() => Mesh?.Dispose();
    }

    internal sealed class PreparedFlare : IDisposable
    {
        public LensFlarePreview Definition;
        public Dictionary<IEntry, MaterialResources> Materials = [];
        public (ExportEntry Export, byte[] Data)[] SourceData;
        public bool HasAnimatedMaterials => Materials.Values.Any(resource => resource.Material.HasFrameDependentUniforms);
        /// <summary>Released: components holding it prepare their flare again</summary>
        public bool Disposed { get; private set; }
        public void Dispose()
        {
            Disposed = true;
            foreach (MaterialResources resource in Materials.Values) resource.Dispose();
            Materials.Clear();
        }
    }

    public PreparedFlare Prepare(IEntry templateEntry)
    {
        ExportEntry template = context.ResolveExportCached(templateEntry);
        if (template?.ClassName != "LensFlare") return null;
        var sourceExports = template.GetAllDescendants().OfType<ExportEntry>().Prepend(template).ToArray();
        if (!templates.TryGetValue(template, out List<PreparedFlare> versions))
            templates[template] = versions = [];
        PreparedFlare cached = versions.LastOrDefault(version => version.SourceData.Length == sourceExports.Length
            && version.SourceData.Select((snapshot, index) => snapshot.Export == sourceExports[index]
                && sourceExports[index].DataReadOnly.SequenceEqual(snapshot.Data)).All(matches => matches));
        if (cached is not null) return cached;

        var prepared = new PreparedFlare
        {
            Definition = new LensFlarePreview(template),
            SourceData = sourceExports.Select(export => (export, export.Data)).ToArray()
        };
        try
        {
            foreach (IEntry materialEntry in prepared.Definition.Elements.Where(element => element.IsEnabled)
                         .SelectMany(element => element.Materials).Where(entry => entry is not null).Distinct())
            {
                try
                {
                    ExportEntry materialExport = context.ResolveExportCached(materialEntry);
                    if (materialExport is null) continue;
                    var material = new MaterialRenderProxy(context, materialExport, VertexFactory);
                    if (material.UnrealVertexShader is null || material.UnrealPixelShader is null) continue;
                    if (!MeshRenderContext.ValidateVertexFactoryInputLayout<LensFlareVertex>(VertexFactory,
                            material.UnrealVertexShader.ShaderByteCode, out string error))
                    {
                        Debug.WriteLine($"Lens flare {materialEntry.InstancedFullPath}: {error}");
                        continue;
                    }
                    VfxGameShaderRenderer.LoadMaterialTextures(context, material);
                    prepared.Materials[materialEntry] = new MaterialResources
                    {
                        Material = material,
                        Mesh = new Mesh<LensFlareVertex>(context.Device,
                            [new Triangle(0, 1, 2), new Triangle(0, 2, 3)],
                            [new(), new(), new(), new()], isDynamic: true),
                        Blend = VfxGameShaderRenderer.CreateBlendDescription(material.BlendMode)
                    };
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Lens flare {materialEntry.InstancedFullPath}: {exception.Message}");
                }
            }
            versions.Add(prepared);
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    /// <summary>Draws a flare's elements (from the translucent queue). Vertices are relative to the source, for precision</summary>
    public void Render(PreparedFlare flare, Matrix4x4 localToWorld, Vector4 sourceColor)
    {
        if (flare is null || flare.Disposed || flare.Materials.Count == 0) return;
        SceneCamera camera = context.Camera;
        Vector3 eye = camera.EyePosition;
        Vector3 viewForward = camera.CameraForward;
        Vector3 source = localToWorld.Translation;
        Vector3 sourceToCamera = eye - source;
        float intensity = flare.Definition.GetIntensity(sourceToCamera, Vector3.TransformNormal(Vector3.UnitX, localToWorld));
        float depth = Vector3.Dot(source - eye, viewForward);
        if (depth <= camera.ZNear || intensity <= 0) return;
        Vector4 clip = context.WorldToScreen(source);
        if (clip.W <= 0) return;
        var sourceNdc = new Vector2(clip.X, clip.Y) / clip.W;
        float distance = sourceToCamera.Length();
        float worldScale = camera.IsOrthographic ? camera.OrthoWidth : depth;
        float actorScale = Vector3.TransformNormal(Vector3.UnitX, localToWorld).Length();
        float occlusion = flare.Definition.GetOcclusion(1);
        Matrix4x4 sourceTranslation = Matrix4x4.CreateTranslation(source);
        // The point on the view axis at the source's depth: reflections are spread along the ray from the source through it
        Vector3 axisPoint = eye + viewForward * depth;
        foreach (LensFlareElementPreview element in flare.Definition.Elements)
        {
            if (!element.IsEnabled) continue;
            float radialDistance = element.NormalizeRadialDistance
                ? MathF.Max(MathF.Abs(sourceNdc.X), MathF.Abs(sourceNdc.Y)) : sourceNdc.Length();
            LensFlareElementSample sample = element.Evaluate(radialDistance, distance, sourceColor);
            if (sample.Material is null || !flare.Materials.TryGetValue(sample.Material, out MaterialResources resource)
                || sample.Color.W <= 0) continue;
            Vector3 center = Vector3.Lerp(source, axisPoint, element.RayDistance)
                             + Vector3.TransformNormal(sample.Offset, localToWorld) - source;
            Vector2 size = sample.Size * worldScale * actorScale;
            if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || !float.IsFinite(sample.Rotation)) continue;
            var parameters = new Vector4(element.RayDistance, radialDistance, distance, intensity);
            var vertices = resource.Mesh.Vertices;
            vertices[0] = new LensFlareVertex(center, size, sample.Rotation, new Vector2(0, 0), parameters, sample.Color);
            vertices[1] = new LensFlareVertex(center, size, sample.Rotation, new Vector2(1, 0), parameters, sample.Color);
            vertices[2] = new LensFlareVertex(center, size, sample.Rotation, new Vector2(1, 1), parameters, sample.Color);
            vertices[3] = new LensFlareVertex(center, size, sample.Rotation, new Vector2(0, 1), parameters, sample.Color);
            resource.Mesh.LocalToWorld = sourceTranslation;
            resource.Mesh.UpdateVertices(context.ImmediateContext);
            //the vertices are the element's center: its size, for texture streaming's on-screen size estimate
            VfxGameShaderRenderer.InflateBounds(resource.Mesh, MathF.Max(size.X, size.Y));
            resource.Material.LensFlareOcclusion = occlusion;
            VfxGameShaderRenderer.RenderNativeMaterial(context, resource.Material, resource.Mesh, resource.Blend,
                depthTest: true, depthWrite: false, indexCount: 6, isParticleSprite: true);
        }
    }

    public void Clear()
    {
        foreach (PreparedFlare flare in templates.Values.SelectMany(versions => versions)) flare.Dispose();
        templates.Clear();
    }

    public void Dispose()
    {
        Clear();
        //the depth copy and depth state effects share (LevelVfxRenderer releases them too, whichever goes first)
        context.DisposeEffectsResources();
    }
}
