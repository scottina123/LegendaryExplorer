using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
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

    private const string MeshShader = """
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
    if (alpha <= 0.0001) color = 0;
    if ((Flags & 1) != 0) return float4(color, alpha);
    float3 lighting = saturate(0.35 + 0.65 * saturate(dot(normalize(input.normal), normalize(float3(0.4, 0.5, 0.75)))));
    return float4(color * lighting, alpha);
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
        public Mesh<WorldVertex> Mesh;
        public List<MeshSection> Sections = [];
        public VfxBounds LocalBounds;

        public void Dispose()
        {
            Preview?.Dispose();
            GameShaderPreview?.Dispose();
            StaticMesh = null;
            Preview = null;
            GameShaderPreview = null;
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
                    || material is not LEShaderPreviewMaterial { CanRender: true })
                {
                    warning = $"{materialName} has no compatible in-game local vertex factory shader.";
                    preview.Dispose();
                    return false;
                }

                ModelPreviewSection section = lod.Sections[sectionIndex];
                section.MaterialName = materialName;
                lod.Sections[sectionIndex] = section;
            }

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

    /// <summary>
    /// Draws every live mesh particle using the Meshplorer/Morph Editor in-game material pipeline.
    /// </summary>
    public static bool RenderGameShader(
        MeshRenderContext context,
        VfxEmitterState emitter,
        MeshEmitterResources resources,
        Matrix4x4 previewTransform)
    {
        if (resources?.GameShaderPreview is null)
        {
            return false;
        }

        IReadOnlyList<VfxParticle> particles = emitter.Particles;
        if (particles.Count == 0)
        {
            return true;
        }
        if (emitter.Definition.UseMaxDrawCount
            && emitter.Definition.MaxDrawCount >= 0
            && particles.Count > emitter.Definition.MaxDrawCount)
        {
            particles = [.. particles.Take(emitter.Definition.MaxDrawCount)];
        }

        Matrix4x4 transform = previewTransform == default ? Matrix4x4.Identity : previewTransform;
        if (resources.Sections.Any(section => !section.IsOpaque))
        {
            var sortedParticles = new List<VfxParticle>(particles);
            VfxBillboardRenderer.SortParticles(sortedParticles, emitter.Definition.SortMode, context.Camera.Position, transform);
            particles = sortedParticles;
        }

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
            resources.GameShaderPreview.UpdateLocalToWorld(model);
            resources.GameShaderPreview.Render(RenderPass.ANY, context, 0);
        }
        return true;
    }

    public void Render(
        MeshRenderContext context,
        VfxEmitterState emitter,
        MeshEmitterResources resources,
        IReadOnlyList<VfxParticle> particleSource,
        Matrix4x4 previewTransform)
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

        // ParticleModuleRequired.MaxDrawCount clamps how many mesh instances are drawn.
        if (emitter.Definition.UseMaxDrawCount && emitter.Definition.MaxDrawCount >= 0 && particles.Count > emitter.Definition.MaxDrawCount)
        {
            particles = [.. particles.Take(emitter.Definition.MaxDrawCount)];
        }

        VfxMeshEmitterDefinition meshDefinition = emitter.Definition.MeshEmitter;
        Matrix4x4 transform = previewTransform == default ? Matrix4x4.Identity : previewTransform;

        // Opaque and masked sections can be drawn in any order, but blended sections have to be drawn
        // back-to-front. Sorting once per emitter is enough because every section shares the same transform.
        List<VfxParticle> sortedParticles = null;
        if (resources.Sections.Any(section => !section.IsOpaque))
        {
            sortedParticles = [.. particles];
            VfxBillboardRenderer.SortParticles(sortedParticles, emitter.Definition.SortMode, context.Camera.Position, transform);
        }

        // Sections form the outer loop so that blend/depth/texture state is only set once per section.
        foreach (MeshSection section in resources.Sections)
        {
            if (section.Texture?.TextureView is null)
            {
                continue;
            }

            ShaderResourceView textureView = section.Texture.TextureView;
            foreach (VfxParticle particle in section.IsOpaque ? particles : sortedParticles ?? particles)
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
                context.ImmediateContext.OutputMerger.SetDepthStencilState(section.DepthState);
                effect.RenderObject(
                    context.ImmediateContext,
                    resources.Mesh,
                    (int)section.Section.StartIndex,
                    (int)section.Section.TriangleCount * 3,
                    textureView);
            }
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
        if (material.BlendMode is VfxBlendMode.Masked or VfxBlendMode.SoftMasked)
        {
            flags |= MaterialMaskedFlag;
        }
        return flags | ((int)material.OpacitySource << OpacitySourceShift);
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
