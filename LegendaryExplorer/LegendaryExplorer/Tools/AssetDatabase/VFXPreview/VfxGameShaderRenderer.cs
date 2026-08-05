using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Packages;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

/// <summary>
/// Renders particle sprites through the same local-vertex-factory material path used by Meshplorer and
/// Morph Editor. Resources are created only while the experimental in-game shader option is enabled.
/// </summary>
public sealed class VfxGameShaderRenderer : IDisposable
{
    private sealed class SpriteResources : IDisposable
    {
        public ModelPreview<LEVertex> Preview;
        public int ParticleCapacity;

        public void Dispose()
        {
            Preview?.Dispose();
            Preview = null;
            ParticleCapacity = 0;
        }
    }

    private readonly Dictionary<VfxEmitterDefinition, SpriteResources> spriteEmitters = [];

    public bool TryLoadSprite(
        MeshRenderContext context,
        VfxEmitterDefinition emitter,
        ExportEntry materialExport,
        out string warning)
    {
        warning = null;
        if (materialExport is null)
        {
            return false;
        }

        try
        {
            const int initialParticleCapacity = 1;
            Mesh<LEVertex> mesh = CreateSpriteMesh(context, initialParticleCapacity);
            var preload = new PreloadedModelData
            {
                sections = [new ModelPreviewSection(materialExport.InstancedFullPath, 0, 0)],
                Materials = [materialExport]
            };
            var preview = new ModelPreview<LEVertex>(context, mesh, preload);
            if (!preview.Materials.TryGetValue(materialExport.InstancedFullPath, out ModelPreviewMaterial<LEVertex> material)
                || material is not LEShaderPreviewMaterial { CanRender: true })
            {
                preview.Dispose();
                warning = $"{materialExport.InstancedFullPath} has no compatible in-game local vertex factory shader.";
                return false;
            }

            spriteEmitters[emitter] = new SpriteResources
            {
                Preview = preview,
                ParticleCapacity = initialParticleCapacity
            };
            return true;
        }
        catch (Exception exception)
        {
            warning = $"{materialExport.InstancedFullPath} could not load its in-game shader ({exception.Message}).";
            return false;
        }
    }

    public bool TryRenderSprite(
        MeshRenderContext context,
        VfxEmitterState emitter,
        Matrix4x4 previewTransform)
    {
        if (!spriteEmitters.TryGetValue(emitter.Definition, out SpriteResources resources))
        {
            return false;
        }

        List<VfxParticle> particles = [.. emitter.Particles];
        VfxBillboardRenderer.SortParticles(
            particles,
            emitter.Definition.SortMode,
            context.Camera.Position,
            previewTransform);
        if (emitter.Definition.UseMaxDrawCount
            && emitter.Definition.MaxDrawCount >= 0
            && particles.Count > emitter.Definition.MaxDrawCount)
        {
            particles.RemoveRange(emitter.Definition.MaxDrawCount, particles.Count - emitter.Definition.MaxDrawCount);
        }
        if (particles.Count == 0)
        {
            return true;
        }

        EnsureSpriteCapacity(context, resources, particles.Count);
        ModelPreviewLOD<LEVertex> lod = resources.Preview.LODs[0];
        Mesh<LEVertex> mesh = lod.Mesh;
        Matrix4x4 transform = previewTransform == default ? Matrix4x4.Identity : previewTransform;
        Span<Vector3> corners = stackalloc Vector3[4];
        for (int particleIndex = 0; particleIndex < particles.Count; particleIndex++)
        {
            VfxParticle particle = particles[particleIndex];
            VfxBillboardBasis basis = VfxBillboardMath.CreateBasis(
                context.Camera.CameraRight,
                context.Camera.CameraUp,
                context.Camera.CameraForward,
                particle.Velocity,
                emitter.Definition.ScreenAlignment,
                emitter.Definition.AxisLock,
                particle.Rotation);
            VfxParticle renderParticle = particle;
            renderParticle.Position = Vector3.Transform(particle.Position + particle.OrbitOffset, transform);
            VfxBillboardMath.CreateQuad(renderParticle, emitter.Definition, basis, corners);
            VfxBillboardRenderer.GetSubUVs(
                emitter.Definition,
                particle.SubImageIndex,
                out Vector2 uvMinimum,
                out Vector2 uvMaximum);

            int vertexStart = particleIndex * 4;
            Vector4 normal = new(basis.Normal, 1);
            mesh.Vertices[vertexStart] = CreateVertex(corners[0], basis.Right, normal, particle.Color, new Vector2(uvMinimum.X, uvMinimum.Y));
            mesh.Vertices[vertexStart + 1] = CreateVertex(corners[1], basis.Right, normal, particle.Color, new Vector2(uvMaximum.X, uvMinimum.Y));
            mesh.Vertices[vertexStart + 2] = CreateVertex(corners[2], basis.Right, normal, particle.Color, new Vector2(uvMaximum.X, uvMaximum.Y));
            mesh.Vertices[vertexStart + 3] = CreateVertex(corners[3], basis.Right, normal, particle.Color, new Vector2(uvMinimum.X, uvMaximum.Y));
        }

        // Dynamic buffers keep their high-water capacity. Duplicate the final live vertex into the unused
        // slots so material expressions based on object bounds are not pulled back toward the origin.
        int usedVertexCount = particles.Count * 4;
        LEVertex boundsPlaceholder = mesh.Vertices[usedVertexCount - 1];
        for (int vertexIndex = usedVertexCount; vertexIndex < mesh.Vertices.Count; vertexIndex++)
        {
            mesh.Vertices[vertexIndex] = boundsPlaceholder;
        }

        mesh.UpdateVertices(context.ImmediateContext);
        ModelPreviewSection section = lod.Sections[0];
        section.TriangleCount = (uint)(particles.Count * 2);
        lod.Sections[0] = section;
        resources.Preview.Render(RenderPass.ANY, context, 0);
        return true;
    }

    private static void EnsureSpriteCapacity(MeshRenderContext context, SpriteResources resources, int particleCount)
    {
        if (particleCount <= resources.ParticleCapacity)
        {
            return;
        }

        int capacity = resources.ParticleCapacity;
        while (capacity < particleCount)
        {
            capacity *= 2;
        }

        ModelPreviewLOD<LEVertex> lod = resources.Preview.LODs[0];
        lod.Mesh.Dispose();
        lod.Mesh = CreateSpriteMesh(context, capacity);
        resources.ParticleCapacity = capacity;
    }

    private static Mesh<LEVertex> CreateSpriteMesh(MeshRenderContext context, int particleCapacity)
    {
        var triangles = new List<Triangle>(particleCapacity * 2);
        var vertices = new List<LEVertex>(particleCapacity * 4);
        LEVertex placeholder = CreateVertex(Vector3.Zero, Vector3.UnitY, new Vector4(Vector3.UnitX, 1), Vector4.One, Vector2.Zero);
        for (int particleIndex = 0; particleIndex < particleCapacity; particleIndex++)
        {
            uint vertexStart = (uint)(particleIndex * 4);
            vertices.Add(placeholder);
            vertices.Add(placeholder);
            vertices.Add(placeholder);
            vertices.Add(placeholder);
            triangles.Add(new Triangle(vertexStart, vertexStart + 1, vertexStart + 2));
            triangles.Add(new Triangle(vertexStart, vertexStart + 2, vertexStart + 3));
        }
        return new Mesh<LEVertex>(context.Device, triangles, vertices, isDynamic: true);
    }

    private static LEVertex CreateVertex(Vector3 position, Vector3 tangent, Vector4 normal, Vector4 color, Vector2 uv)
    {
        Fixed4<Vector4> uvs = default;
        uvs[0] = new Vector4(uv, 0, 0);
        return ((LEVertex)LEVertex.Create(position, tangent, normal, uvs)).WithColor(color);
    }

    public void Clear()
    {
        foreach (SpriteResources resources in spriteEmitters.Values)
        {
            resources.Dispose();
        }
        spriteEmitters.Clear();
    }

    public void Dispose() => Clear();
}
