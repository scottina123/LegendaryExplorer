using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

/// <summary>
/// Renders mesh emitters (ParticleModuleTypeDataMesh) by drawing the referenced StaticMesh once per live particle.
/// The geometry, materials and textures all come from the existing Legendary Explorer mesh preview infrastructure
/// (<see cref="ModelPreview{TVertex}"/>, <see cref="Mesh{TVertex}"/>, <see cref="PreviewTextureCache"/>).
/// </summary>
public sealed class VfxMeshRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MeshConstants
    {
        public Matrix4x4 Projection;
        public Matrix4x4 View;
        public Matrix4x4 Model;
        public Vector4 ParticleColor;
        public Vector4 TintAndClip;
        public int Flags;
        public int Padding0;
        public int Padding1;
        public int Padding2;
    }

    private const int MaterialUnlitFlag = 1 << 0;
    private const int MaterialMaskedFlag = 1 << 1;
    private const int OpacitySourceShift = 2;
    private const int BlendModeShift = 5;

    internal const string MeshShader = VfxMaterialBlending.ShaderFunctions + """
struct VS_IN { float4 pos : POSITION0; float3 hitTestID : TANGENT0; float4 normal : NORMAL0; float4 color : COLOR1; float2 uv : TEXCOORD0; };
struct VS_OUT { float4 pos : SV_POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
cbuffer constants { float4x4 projection; float4x4 view; float4x4 model; float4 ParticleColor; float4 TintAndClip; int Flags; int pad0; int pad1; int pad2; };
Texture2D tex : register(t0); SamplerState samstate : register(s0);
VS_OUT VSMain(VS_IN input) { VS_OUT output = (VS_OUT)0; float4 worldPos = mul(float4(input.pos.xyz, 1), model); output.pos = mul(mul(worldPos, view), projection); output.normal = normalize(mul(float4(input.normal.xyz, 0), model).xyz); output.uv = input.uv; return output; }
float4 PSMain(VS_OUT input) : SV_TARGET0
{
    float4 textureSample = tex.Sample(samstate, input.uv);
    int opacitySource = (Flags >> 2) & 7;
    float textureOpacity = opacitySource == 1 ? dot(textureSample.rgb, float3(0.299, 0.587, 0.114)) : opacitySource == 2 ? textureSample.r : opacitySource == 3 ? textureSample.g : opacitySource == 4 ? textureSample.b : opacitySource == 5 ? 1 : textureSample.a;
    float alpha = saturate(textureOpacity * ParticleColor.a);
    if ((Flags & 2) != 0) clip(alpha - TintAndClip.a);
    float3 color = textureSample.rgb * ParticleColor.rgb * TintAndClip.rgb;
    if ((Flags & 1) == 0)
    {
        float3 lighting = saturate(0.35 + 0.65 * saturate(dot(normalize(input.normal), normalize(float3(0.4, 0.5, 0.75)))));
        color *= lighting;
    }
    return ApplyVfxMaterialBlend(color, alpha, ParticleColor.a, (Flags >> 5) & 7, opacitySource);
}
""";

    /// <summary>
    /// A single drawable mesh section, with the material state resolved for it.
    /// </summary>
    public sealed class MeshSection
    {
        public ModelPreviewSection Section;
        public ExportEntry GameShaderMaterial;
        public VfxParticleMaterialDefinition Material = new();
        public PreviewTextureCache.TextureEntry Texture;
        public BlendState BlendState;
        public DepthStencilState DepthState;
        public bool IsOpaque;
    }

    /// <summary>
    /// Everything needed to render one mesh emitter.
    /// </summary>
    public sealed class MeshEmitterResources : IDisposable
    {
        public StaticMesh StaticMesh;
        public ModelPreview<WorldVertex> Preview;
        public ModelPreview<LEVertex> GameShaderPreview;
        public Mesh<LEVertex> GameShaderMesh;
        public Vector4[] GameShaderVertexColors;
        public Mesh<WorldVertex> Mesh;
        public List<MeshSection> Sections = [];
        public VfxBounds LocalBounds;

        public void Dispose()
        {
            Preview?.Dispose();
            GameShaderPreview?.Dispose();
            GameShaderMesh?.Dispose();
            StaticMesh = null;
            Preview = null;
            GameShaderPreview = null;
            GameShaderMesh = null;
            GameShaderVertexColors = null;
            Mesh = null;
            Sections.Clear();
        }
    }

    private GenericEffect<MeshConstants> effect;

    public void CreateResources(MeshRenderContext context)
    {
        effect?.Dispose();
        effect = new GenericEffect<MeshConstants>(context.Device, MeshShader);
    }

    /// <summary>
    /// Loads the StaticMesh referenced by a mesh emitter's type data module.
    /// </summary>
    public static MeshEmitterResources LoadMesh(MeshRenderContext context, VfxMeshEmitterDefinition meshDefinition)
    {
        ExportEntry meshExport = meshDefinition.Mesh switch
        {
            ExportEntry export => export,
            ImportEntry import => EntryImporter.ResolveImport(import, context.PackageCache),
            _ => null
        };
        if (meshExport is null)
        {
            return null;
        }

        var staticMesh = ObjectBinary.From<StaticMesh>(meshExport);
        if (staticMesh.LODModels.Length == 0)
        {
            return null;
        }

        var preview = new ModelPreview<WorldVertex>(context, staticMesh, 0);
        if (preview.LODs.Count == 0)
        {
            preview.Dispose();
            return null;
        }

        ModelPreviewLOD<WorldVertex> lod = preview.LODs[0];
        Vector3 extent = lod.Mesh.BaseBounds.BoxExtent;
        Vector3 origin = lod.Mesh.BaseBounds.Origin;
        var resources = new MeshEmitterResources
        {
            StaticMesh = staticMesh,
            Preview = preview,
            Mesh = lod.Mesh,
            LocalBounds = new VfxBounds(origin - extent, origin + extent)
        };
        foreach (ModelPreviewSection section in lod.Sections)
        {
            resources.Sections.Add(new MeshSection { Section = section });
        }
        meshDefinition.LocalBounds = resources.LocalBounds;
        return resources;
    }

    /// <summary>
    /// Creates the same LE-vertex model/material preview used by Meshplorer and Morph Editor, then applies
    /// the mesh-emitter material overrides already resolved by the VFX preview.
    /// </summary>
    public static bool TryLoadGameShaderPreview(
        MeshRenderContext context,
        MeshEmitterResources resources,
        out string warning)
    {
        warning = null;
        resources.GameShaderPreview?.Dispose();
        resources.GameShaderPreview = null;
        resources.GameShaderMesh?.Dispose();
        resources.GameShaderMesh = null;
        resources.GameShaderVertexColors = null;
        if (resources.StaticMesh is null)
        {
            return false;
        }

        ModelPreview<LEVertex> preview = null;
        try
        {
            preview = new ModelPreview<LEVertex>(context, resources.StaticMesh, 0);
            if (preview.LODs.Count == 0 || preview.LODs[0].Sections.Count != resources.Sections.Count)
            {
                warning = "The mesh could not be prepared for the in-game shader preview.";
                preview.Dispose();
                return false;
            }

            ModelPreviewLOD<LEVertex> lod = preview.LODs[0];
            for (int sectionIndex = 0; sectionIndex < resources.Sections.Count; sectionIndex++)
            {
                ExportEntry materialExport = resources.Sections[sectionIndex].GameShaderMaterial;
                if (materialExport is null || !preview.AddMaterial(context, materialExport))
                {
                    warning = $"Mesh section {sectionIndex} has no material for the in-game shader preview.";
                    preview.Dispose();
                    return false;
                }

                string materialName = materialExport.InstancedFullPath;
                if (!preview.Materials.TryGetValue(materialName, out ModelPreviewMaterial<LEVertex> material)
                    || material is not LEShaderPreviewMaterial { CanRender: true } shaderPreviewMaterial)
                {
                    warning = $"{materialName} has no compatible in-game local vertex factory shader.";
                    preview.Dispose();
                    return false;
                }
                if (!MeshRenderContext.ValidateVertexFactoryInputLayout<LEVertex>(
                        "FLocalVertexFactory",
                        shaderPreviewMaterial.RenderProxy.UnrealVertexShader.ShaderByteCode,
                        out string inputError))
                {
                    warning = $"{materialName} rejected FLocalVertexFactory: {inputError}";
                    preview.Dispose();
                    return false;
                }

                ModelPreviewSection section = lod.Sections[sectionIndex];
                section.MaterialName = materialName;
                lod.Sections[sectionIndex] = section;
            }

            // Particle colors vary per instance. Keep this dynamic vertex stream private to the VFX preview,
            // so uploading a particle's color never modifies cached StaticMesh geometry used by other tools.
            Vector4[] vertexColors = ReadMeshVertexColors(resources.StaticMesh.LODModels[0], lod.Mesh.Vertices.Count);
            resources.GameShaderMesh = new Mesh<LEVertex>(context.Device,
                [.. lod.Mesh.Triangles], [.. lod.Mesh.Vertices], isDynamic: true);
            resources.GameShaderVertexColors = vertexColors;
            resources.GameShaderPreview = preview;
            return true;
        }
        catch (Exception exception)
        {
            preview?.Dispose();
            warning = $"The mesh in-game shader could not be loaded ({exception.Message}).";
            return false;
        }
    }

    public static bool HasBlendedSections(MeshEmitterResources resources, bool useGameShader)
    {
        if (useGameShader && resources.GameShaderPreview is { } preview && resources.GameShaderMesh is not null)
        {
            return preview.LODs[0].Sections.Any(section =>
                preview.Materials[section.MaterialName] is LEShaderPreviewMaterial material
                && material.RenderProxy.BlendMode is not (EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked));
        }
        return resources.Sections.Any(section => !section.IsOpaque);
    }

    internal static Vector4[] ReadMeshVertexColors(StaticMeshRenderData lod, int vertexCount)
    {
        var colors = new Vector4[vertexCount];
        bool hasColor = false;
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            LegendaryExplorerCore.SharpDX.Color color = lod.ColorVertexBuffer?.VertexData is { } separateColors
                && vertexIndex < separateColors.Length
                    ? separateColors[vertexIndex]
                    : lod.VertexBuffer.VertexData[vertexIndex].Color;
            colors[vertexIndex] = new Vector4(color.R, color.G, color.B, color.A) / 255f;
            hasColor |= colors[vertexIndex] != Vector4.Zero;
        }
        // An absent color stream is represented by an all-zero buffer in some cooked meshes. Supply a
        // neutral vertex color in that case, while preserving every authored color in populated streams.
        if (!hasColor)
        {
            Array.Fill(colors, Vector4.One);
        }
        return colors;
    }

    /// <summary>
    /// Draws live mesh particles with their native material shaders and VFX blend/depth state.
    /// </summary>
    public static bool RenderGameShader(
        MeshRenderContext context,
        VfxEmitterState emitter,
        MeshEmitterResources resources,
        Matrix4x4 previewTransform,
        IReadOnlyList<VfxParticle> particleSource = null,
        bool? opaquePass = null)
    {
        if (resources?.GameShaderPreview is null || resources.GameShaderMesh is null)
        {
            return false;
        }

        IReadOnlyList<VfxParticle> particles = particleSource ?? emitter.Particles;
        if (particles.Count == 0)
        {
            return true;
        }
        Matrix4x4 transform = previewTransform == default ? Matrix4x4.Identity : previewTransform;
        if (particleSource is null && HasBlendedSections(resources, useGameShader: true))
        {
            var sortedParticles = new List<VfxParticle>(particles);
            VfxBillboardRenderer.SortParticles(sortedParticles, emitter.Definition.SortMode, context.Camera.Position, transform,
                context.Camera.CameraForward);
            particles = sortedParticles;
        }
        if (particleSource is null && emitter.Definition.UseMaxDrawCount
            && emitter.Definition.MaxDrawCount >= 0
            && particles.Count > emitter.Definition.MaxDrawCount)
        {
            particles = [.. particles.Take(emitter.Definition.MaxDrawCount)];
        }
        if (opaquePass is null)
        {
            // Complete depth-writing surfaces before any transparency even for callers that do not use
            // the preview's shared render queue.
            RenderGameShader(context, emitter, resources, transform, particles, opaquePass: true);
            RenderGameShader(context, emitter, resources, transform, particles, opaquePass: false);
            return true;
        }

        ModelPreviewLOD<LEVertex> lod = resources.GameShaderPreview.LODs[0];
        if (opaquePass is { } requestedOpaque && !lod.Sections.Any(section =>
                resources.GameShaderPreview.Materials[section.MaterialName] is LEShaderPreviewMaterial material
                && (material.RenderProxy.BlendMode is EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked) == requestedOpaque))
        {
            return true;
        }
        Mesh<LEVertex> mesh = resources.GameShaderMesh;
        foreach (VfxParticle particle in particles)
        {
            Matrix4x4 model = VfxMeshMath.CreateParticleTransform(
                particle,
                emitter.Definition,
                emitter.Definition.MeshEmitter,
                context.Camera.Position,
                context.Camera.CameraRight,
                context.Camera.CameraUp,
                context.Camera.CameraForward,
                transform);
            mesh.LocalToWorld = model;
            for (int vertexIndex = 0; vertexIndex < mesh.Vertices.Count; vertexIndex++)
            {
                // The local factory consumes COLOR1. Keep authored mesh color and apply the instance color
                // through that stream so vertex-color material expressions receive particle fades as well.
                mesh.Vertices[vertexIndex] = lod.Mesh.Vertices[vertexIndex]
                    .WithColor(resources.GameShaderVertexColors[vertexIndex] * particle.Color);
            }
            mesh.UpdateVertices(context.ImmediateContext);
            for (int sectionIndex = 0; sectionIndex < lod.Sections.Count; sectionIndex++)
            {
                MeshSection resolvedSection = resources.Sections[sectionIndex];
                ModelPreviewSection section = lod.Sections[sectionIndex];
                if (resources.GameShaderPreview.Materials[section.MaterialName] is not LEShaderPreviewMaterial shaderMaterial
                    || !shaderMaterial.CanRender || !shaderMaterial.RenderProxy.HasRequiredTextures(context))
                {
                    continue;
                }
                MaterialRenderProxy material = shaderMaterial.RenderProxy;
                bool isOpaque = material.BlendMode is EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked;
                if (opaquePass is { } drawOpaque && isOpaque != drawOpaque)
                {
                    continue;
                }
                try
                {
                    // Native shaders retain their cooked alpha. The general model preview recompiles alpha
                    // for inspection and uses default depth writes, which breaks translucent mesh particles.
                    VfxGameShaderRenderer.RenderNativeMaterial(context, material, mesh,
                        VfxGameShaderRenderer.CreateBlendDescription(material.BlendMode),
                        !resolvedSection.Material.DisableDepthTest,
                        isOpaque,
                        (int)section.TriangleCount * 3,
                        startIndex: (int)section.StartIndex);
                }
                finally
                {
                    context.ResetTextureSamplers();
                }
            }
        }
        return true;
    }

    public void Render(
        MeshRenderContext context,
        VfxEmitterState emitter,
        MeshEmitterResources resources,
        IReadOnlyList<VfxParticle> particleSource,
        Matrix4x4 previewTransform,
        bool? opaquePass = null)
    {
        if (effect is null || resources?.Mesh is null)
        {
            return;
        }
        IReadOnlyList<VfxParticle> particles = particleSource ?? emitter.Particles;
        if (particles.Count == 0)
        {
            return;
        }

        VfxMeshEmitterDefinition meshDefinition = emitter.Definition.MeshEmitter;
        Matrix4x4 transform = previewTransform == default ? Matrix4x4.Identity : previewTransform;

        // Opaque and masked sections can be drawn in any order, but blended sections have to be drawn
        // back-to-front. Sorting once per emitter is enough because every section shares the same transform.
        if (particleSource is null && resources.Sections.Any(section => !section.IsOpaque))
        {
            var sortedParticles = new List<VfxParticle>(particles);
            VfxBillboardRenderer.SortParticles(sortedParticles, emitter.Definition.SortMode, context.Camera.Position, transform,
                context.Camera.CameraForward);
            particles = sortedParticles;
        }
        // Clamp after the authored ordering, matching the sprite renderer and shared preview queue.
        if (particleSource is null && emitter.Definition.UseMaxDrawCount && emitter.Definition.MaxDrawCount >= 0 && particles.Count > emitter.Definition.MaxDrawCount)
        {
            particles = [.. particles.Take(emitter.Definition.MaxDrawCount)];
        }

        void RenderParticles(bool drawOpaque)
        {
            MeshSection[] sections = resources.Sections
                .Where(section => section.IsOpaque == drawOpaque && section.Texture?.TextureView is not null)
                .ToArray();
            // All sections of a farther particle must be blended before any section of a nearer one.
            // A section-outer loop changes that order when particles have multiple translucent materials.
            foreach (VfxParticle particle in particles)
            {
                Matrix4x4 model = VfxMeshMath.CreateParticleTransform(
                    particle,
                    emitter.Definition,
                    meshDefinition,
                    context.Camera.Position,
                    context.Camera.CameraRight,
                    context.Camera.CameraUp,
                    context.Camera.CameraForward,
                    transform);

                foreach (MeshSection section in sections)
                {
                    var constants = new MeshConstants
                    {
                        Projection = context.Camera.ProjectionMatrix,
                        View = context.Camera.ViewMatrix,
                        Model = model,
                        ParticleColor = particle.Color,
                        TintAndClip = new Vector4(
                            section.Material.EmissiveTint.X,
                            section.Material.EmissiveTint.Y,
                            section.Material.EmissiveTint.Z,
                            section.Material.OpacityMaskClipValue),
                        Flags = BuildFlags(section.Material)
                    };
                    effect.PrepDraw(context.ImmediateContext, section.BlendState ?? context.AlphaBlendState, constants);
                    context.ImmediateContext.PixelShader.SetSampler(0, context.SampleState);
                    context.ImmediateContext.OutputMerger.SetDepthStencilState(section.DepthState);
                    effect.RenderObject(
                        context.ImmediateContext,
                        resources.Mesh,
                        (int)section.Section.StartIndex,
                        (int)section.Section.TriangleCount * 3,
                        section.Texture.TextureView);
                }
            }
        }
        if (opaquePass != false)
        {
            RenderParticles(drawOpaque: true);
        }
        if (opaquePass != true)
        {
            RenderParticles(drawOpaque: false);
        }
        context.ImmediateContext.PixelShader.SetShaderResource(0, null);
        context.ImmediateContext.OutputMerger.SetDepthStencilState(null);
    }

    private static int BuildFlags(VfxParticleMaterialDefinition material)
    {
        int flags = 0;
        if (material.IsUnlit)
        {
            flags |= MaterialUnlitFlag;
        }
        if (material.BlendMode == VfxBlendMode.Masked)
        {
            flags |= MaterialMaskedFlag;
        }
        return flags | ((int)material.OpacitySource << OpacitySourceShift)
            | ((int)material.BlendMode << BlendModeShift);
    }

    /// <summary>
    /// Returns the world-space bounds of every live particle of a mesh emitter.
    /// </summary>
    public static bool TryGetBounds(VfxEmitterState emitter, MeshEmitterResources resources, Matrix4x4 previewTransform, Vector3 cameraPosition, Vector3 cameraRight, Vector3 cameraUp, Vector3 cameraForward, out VfxBounds bounds)
    {
        bounds = default;
        if (resources is null || emitter.Definition.MeshEmitter is null || emitter.Particles.Count == 0)
        {
            return false;
        }

        Matrix4x4 transform = previewTransform == default ? Matrix4x4.Identity : previewTransform;
        Vector3 minimum = new(float.MaxValue);
        Vector3 maximum = new(float.MinValue);
        foreach (VfxParticle particle in emitter.Particles)
        {
            Matrix4x4 model = VfxMeshMath.CreateParticleTransform(
                particle,
                emitter.Definition,
                emitter.Definition.MeshEmitter,
                cameraPosition,
                cameraRight,
                cameraUp,
                cameraForward,
                transform);
            VfxBounds particleBounds = VfxBoundsMath.Transform(resources.LocalBounds, model);
            if (!particleBounds.IsValid)
            {
                continue;
            }
            minimum = Vector3.Min(minimum, particleBounds.Minimum);
            maximum = Vector3.Max(maximum, particleBounds.Maximum);
        }

        bounds = new VfxBounds(minimum, maximum);
        return bounds.IsValid;
    }

    public void Dispose()
    {
        effect?.Dispose();
        effect = null;
    }
}
