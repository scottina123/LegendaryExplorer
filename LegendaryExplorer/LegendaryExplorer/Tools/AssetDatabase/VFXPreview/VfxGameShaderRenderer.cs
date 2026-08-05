using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

/// <summary>
/// Renders sprite emitters with UE3's native particle vertex factories. Each resource records the exact
/// factory selected from the material shader map and supplies that factory's complete vertex and constant data.
/// </summary>
public sealed class VfxGameShaderRenderer : IDisposable
{
    internal const string ParticleFactory = "FParticleVertexFactory";
    internal const string ParticleSubUVFactory = "FParticleSubUVVertexFactory";
    internal const string ParticleDynamicFactory = "FParticleDynamicParameterVertexFactory";
    internal const string ParticleSubUVDynamicFactory = "FParticleSubUVDynamicParameterVertexFactory";
    internal const string ParticleBeamTrailFactory = "FParticleBeamTrailVertexFactory";
    internal const string ParticleBeamTrailDynamicFactory = "FParticleBeamTrailDynamicParameterVertexFactory";

    private sealed class SpriteResources : IDisposable
    {
        public Mesh<ParticleVertex> Mesh;
        public MaterialRenderProxy Material;
        public RenderTargetBlendDescription BlendDescription;
        public string VertexFactoryType;
        public bool UsesSubUV;
        public bool UsesDynamicParameter;
        public bool DepthTest;
        public bool DepthWrite;
        public int ParticleCapacity;

        public void Dispose()
        {
            Mesh?.Dispose();
            Mesh = null;
            Material = null;
            ParticleCapacity = 0;
        }
    }

    private sealed class BeamTrailResources : IDisposable
    {
        public Mesh<ParticleBeamTrailVertex> Mesh;
        public MaterialRenderProxy Material;
        public RenderTargetBlendDescription BlendDescription;
        public bool DepthTest;
        public bool DepthWrite;
        public int SegmentCapacity;

        public void Dispose()
        {
            Mesh?.Dispose();
            Mesh = null;
            Material = null;
            SegmentCapacity = 0;
        }
    }

    private readonly record struct RibbonSegment(
        Vector3 Start,
        Vector3 End,
        float StartWidth,
        float EndWidth,
        Vector4 StartColor,
        Vector4 EndColor,
        Vector4 DynamicParameter,
        float Rotation,
        float StartU,
        float EndU);

    private readonly Dictionary<VfxEmitterDefinition, SpriteResources> spriteEmitters = [];
    private readonly Dictionary<VfxEmitterDefinition, BeamTrailResources> beamTrailEmitters = [];

    internal static string SelectSpriteVertexFactory(VfxEmitterDefinition emitter)
    {
        bool subUV = UsesSubUV(emitter);
        bool dynamic = emitter.ParticleMaterial.UsesDynamicParameter;
        return (subUV, dynamic) switch
        {
            (false, false) => ParticleFactory,
            (true, false) => ParticleSubUVFactory,
            (false, true) => ParticleDynamicFactory,
            (true, true) => ParticleSubUVDynamicFactory
        };
    }

    internal static string SelectBeamTrailVertexFactory(VfxEmitterDefinition emitter)
        => emitter.ParticleMaterial.UsesDynamicParameter
            ? ParticleBeamTrailDynamicFactory
            : ParticleBeamTrailFactory;

    private static IEnumerable<string> GetSpriteVertexFactoryCandidates(VfxEmitterDefinition emitter)
    {
        bool subUV = UsesSubUV(emitter);
        bool dynamic = emitter.ParticleMaterial.UsesDynamicParameter;
        if (subUV)
        {
            yield return dynamic ? ParticleSubUVDynamicFactory : ParticleSubUVFactory;
            yield return dynamic ? ParticleSubUVFactory : ParticleSubUVDynamicFactory;
        }
        else
        {
            yield return dynamic ? ParticleDynamicFactory : ParticleFactory;
            yield return dynamic ? ParticleFactory : ParticleDynamicFactory;
        }
    }

    private static IEnumerable<string> GetBeamTrailVertexFactoryCandidates(VfxEmitterDefinition emitter)
    {
        yield return SelectBeamTrailVertexFactory(emitter);
        yield return emitter.ParticleMaterial.UsesDynamicParameter
            ? ParticleBeamTrailFactory
            : ParticleBeamTrailDynamicFactory;
    }

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

        var failures = new List<string>();
        foreach (string candidateFactory in GetSpriteVertexFactoryCandidates(emitter))
        {
            try
            {
                var material = new MaterialRenderProxy(context, materialExport, candidateFactory);
                if (material.UnrealVertexShader is null || material.UnrealPixelShader is null)
                {
                    failures.Add($"{candidateFactory}: no base-pass shader");
                    continue;
                }
                if (!string.Equals(material.UnrealVertexShader.VertexFactoryParameters.VertexFactoryType.Name,
                        candidateFactory, StringComparison.Ordinal))
                {
                    failures.Add($"{candidateFactory}: mismatched shader map");
                    continue;
                }
                if (!MeshRenderContext.ValidateVertexFactoryInputLayout<ParticleVertex>(
                        candidateFactory, material.UnrealVertexShader.ShaderByteCode, out string inputError))
                {
                    failures.Add($"{candidateFactory}: {inputError}");
                    continue;
                }

                LoadMaterialTextures(context, material);
                ConfigureFactoryConstants(material.ParticleFactoryParameters, emitter);

                const int initialParticleCapacity = 1;
                spriteEmitters[emitter] = new SpriteResources
                {
                    Mesh = CreateSpriteMesh(context, initialParticleCapacity),
                    Material = material,
                    BlendDescription = CreateBlendDescription(material.BlendMode),
                    VertexFactoryType = candidateFactory,
                    UsesSubUV = candidateFactory.Contains("SubUV", StringComparison.Ordinal),
                    UsesDynamicParameter = candidateFactory.Contains("DynamicParameter", StringComparison.Ordinal),
                    DepthTest = !emitter.ParticleMaterial.DisableDepthTest,
                    DepthWrite = material.BlendMode is EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked,
                    ParticleCapacity = initialParticleCapacity
                };
                return true;
            }
            catch (Exception exception)
            {
                failures.Add($"{candidateFactory}: {exception.Message}");
            }
        }
        warning = $"{materialExport.InstancedFullPath} has no compatible native particle shader "
            + $"({string.Join("; ", failures)}).";
        return false;
    }

    public bool TryLoadBeamTrail(
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

        var failures = new List<string>();
        foreach (string candidateFactory in GetBeamTrailVertexFactoryCandidates(emitter))
        {
            try
            {
                var material = new MaterialRenderProxy(context, materialExport, candidateFactory);
                if (material.UnrealVertexShader is null || material.UnrealPixelShader is null)
                {
                    failures.Add($"{candidateFactory}: no base-pass shader");
                    continue;
                }
                if (!string.Equals(material.UnrealVertexShader.VertexFactoryParameters.VertexFactoryType.Name,
                        candidateFactory, StringComparison.Ordinal))
                {
                    failures.Add($"{candidateFactory}: mismatched shader map");
                    continue;
                }
                if (!MeshRenderContext.ValidateVertexFactoryInputLayout<ParticleBeamTrailVertex>(
                        candidateFactory, material.UnrealVertexShader.ShaderByteCode, out string inputError))
                {
                    failures.Add($"{candidateFactory}: {inputError}");
                    continue;
                }

                LoadMaterialTextures(context, material);
                ConfigureFactoryConstants(material.ParticleFactoryParameters, emitter);
                const int initialSegmentCapacity = 1;
                beamTrailEmitters[emitter] = new BeamTrailResources
                {
                    Mesh = CreateBeamTrailMesh(context, initialSegmentCapacity),
                    Material = material,
                    BlendDescription = CreateBlendDescription(material.BlendMode),
                    DepthTest = !emitter.ParticleMaterial.DisableDepthTest,
                    DepthWrite = material.BlendMode is EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked,
                    SegmentCapacity = initialSegmentCapacity
                };
                return true;
            }
            catch (Exception exception)
            {
                failures.Add($"{candidateFactory}: {exception.Message}");
            }
        }
        warning = $"{materialExport.InstancedFullPath} has no compatible native beam/trail shader "
            + $"({string.Join("; ", failures)}).";
        return false;
    }

    private static void LoadMaterialTextures(MeshRenderContext context, MaterialRenderProxy material)
    {
        var textureMap = new Dictionary<string, PreviewTextureCache.TextureEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (IEntry textureEntry in material.Textures)
        {
            PreviewTextureCache.TextureEntry texture = context.TextureCache.LoadTexture(textureEntry, context.PackageCache);
            if (texture is null)
            {
                continue;
            }
            textureMap[textureEntry.FullPath] = texture;
            textureMap[textureEntry.InstancedFullPath] = texture;
        }
        material.TextureMap = textureMap;
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
        Mesh<ParticleVertex> mesh = resources.Mesh;
        Matrix4x4 transform = previewTransform == default ? Matrix4x4.Identity : previewTransform;
        for (int particleIndex = 0; particleIndex < particles.Count; particleIndex++)
        {
            VfxParticle particle = particles[particleIndex];
            Vector3 localPosition = particle.Position + particle.OrbitOffset;
            Vector3 size = GetParticleSize(particle, emitter.Definition);

            // Pivot is not a separate native stream. UE3 bakes it into the particle center before submitting
            // the vertex, using the same authored alignment and rotation that the factory will use.
            VfxBillboardBasis pivotBasis = VfxBillboardMath.CreateBasis(
                context.Camera.CameraRight,
                context.Camera.CameraUp,
                context.Camera.CameraForward,
                particle.Velocity,
                emitter.Definition.ScreenAlignment,
                emitter.Definition.AxisLock,
                particle.Rotation);
            localPosition += pivotBasis.Right * emitter.Definition.PivotOffset.X * size.X;
            localPosition += pivotBasis.Up * emitter.Definition.PivotOffset.Y * size.Y;

            Vector3 worldPosition = Vector3.Transform(localPosition, transform);
            Vector3 oldLocalPosition = localPosition - (particle.Velocity / 60f);
            Vector3 oldWorldPosition = Vector3.Transform(oldLocalPosition, transform);
            GetFrameData(emitter.Definition, particle.SubImageIndex,
                out Vector2 currentMinimum, out Vector2 currentMaximum,
                out Vector2 nextMinimum, out Vector2 nextMaximum,
                out float interpolation);

            int vertexStart = particleIndex * 4;
            WriteParticleVertex(mesh.Vertices, vertexStart, worldPosition, oldWorldPosition, size, particle,
                new Vector2(0, 0), new Vector2(currentMinimum.X, currentMinimum.Y), new Vector2(nextMinimum.X, nextMinimum.Y), interpolation, resources);
            WriteParticleVertex(mesh.Vertices, vertexStart + 1, worldPosition, oldWorldPosition, size, particle,
                new Vector2(1, 0), new Vector2(currentMaximum.X, currentMinimum.Y), new Vector2(nextMaximum.X, nextMinimum.Y), interpolation, resources);
            WriteParticleVertex(mesh.Vertices, vertexStart + 2, worldPosition, oldWorldPosition, size, particle,
                new Vector2(1, 1), new Vector2(currentMaximum.X, currentMaximum.Y), new Vector2(nextMaximum.X, nextMaximum.Y), interpolation, resources);
            WriteParticleVertex(mesh.Vertices, vertexStart + 3, worldPosition, oldWorldPosition, size, particle,
                new Vector2(0, 1), new Vector2(currentMinimum.X, currentMaximum.Y), new Vector2(nextMinimum.X, nextMaximum.Y), interpolation, resources);
        }

        int usedVertexCount = particles.Count * 4;
        ParticleVertex boundsPlaceholder = mesh.Vertices[usedVertexCount - 1];
        for (int vertexIndex = usedVertexCount; vertexIndex < mesh.Vertices.Count; vertexIndex++)
        {
            mesh.Vertices[vertexIndex] = boundsPlaceholder;
        }
        mesh.LocalToWorld = Matrix4x4.Identity;
        mesh.UpdateVertices(context.ImmediateContext);
        RenderNativeMaterial(
            context,
            resources.Material,
            resources.Mesh,
            resources.BlendDescription,
            resources.DepthTest,
            resources.DepthWrite,
            particles.Count * 6);
        return true;
    }

    public bool TryRenderBeamTrail(
        MeshRenderContext context,
        VfxEmitterState emitter,
        Matrix4x4 previewTransform)
    {
        if (!beamTrailEmitters.TryGetValue(emitter.Definition, out BeamTrailResources resources))
        {
            return false;
        }

        List<RibbonSegment> segments = emitter.Definition.RenderMode == VfxEmitterRenderMode.Beam
            ? BuildBeamSegments(emitter, previewTransform)
            : BuildTrailSegments(emitter, previewTransform);
        if (segments.Count == 0)
        {
            return true;
        }

        EnsureBeamTrailCapacity(context, resources, segments.Count);
        List<ParticleBeamTrailVertex> vertices = resources.Mesh.Vertices;
        for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            RibbonSegment segment = segments[segmentIndex];
            Vector3 direction = segment.End - segment.Start;
            if (direction.LengthSquared() < 0.000001f)
            {
                direction = Vector3.UnitZ;
            }
            else
            {
                direction = Vector3.Normalize(direction);
            }
            Vector3 midpoint = (segment.Start + segment.End) * 0.5f;
            Vector3 viewDirection = context.Camera.Position - midpoint;
            Vector3 side = Vector3.Cross(direction, viewDirection);
            if (side.LengthSquared() < 0.000001f)
            {
                side = Vector3.Cross(direction, context.Camera.CameraUp);
            }
            if (side.LengthSquared() < 0.000001f)
            {
                side = context.Camera.CameraRight;
            }
            side = Vector3.Normalize(side);

            Vector3 startSide = side * (segment.StartWidth * 0.5f);
            Vector3 endSide = side * (segment.EndWidth * 0.5f);
            Vector3 startLeft = segment.Start - startSide;
            Vector3 startRight = segment.Start + startSide;
            Vector3 endRight = segment.End + endSide;
            Vector3 endLeft = segment.End - endSide;
            int vertexStart = segmentIndex * 4;
            WriteBeamTrailVertex(vertices, vertexStart, startLeft, direction, segment.StartU, 0,
                segment.Rotation, segment.StartColor, segment.DynamicParameter);
            WriteBeamTrailVertex(vertices, vertexStart + 1, startRight, direction, segment.StartU, 1,
                segment.Rotation, segment.StartColor, segment.DynamicParameter);
            WriteBeamTrailVertex(vertices, vertexStart + 2, endRight, direction, segment.EndU, 1,
                segment.Rotation, segment.EndColor, segment.DynamicParameter);
            WriteBeamTrailVertex(vertices, vertexStart + 3, endLeft, direction, segment.EndU, 0,
                segment.Rotation, segment.EndColor, segment.DynamicParameter);
        }

        int usedVertexCount = segments.Count * 4;
        ParticleBeamTrailVertex placeholder = vertices[usedVertexCount - 1];
        for (int vertexIndex = usedVertexCount; vertexIndex < vertices.Count; vertexIndex++)
        {
            vertices[vertexIndex] = placeholder;
        }
        resources.Mesh.LocalToWorld = Matrix4x4.Identity;
        resources.Mesh.UpdateVertices(context.ImmediateContext);
        RenderNativeMaterial(
            context,
            resources.Material,
            resources.Mesh,
            resources.BlendDescription,
            resources.DepthTest,
            resources.DepthWrite,
            segments.Count * 6);
        return true;
    }

    private static void WriteBeamTrailVertex(
        List<ParticleBeamTrailVertex> vertices,
        int index,
        Vector3 position,
        Vector3 direction,
        float u,
        float v,
        float rotation,
        Vector4 color,
        Vector4 dynamicParameter)
    {
        // The native factory derives its tangent frame from POSITION-NORMAL. Supplying a unit step behind the
        // expanded edge makes that vector exactly the authored ribbon direction. TEXCOORD0 and TEXCOORD1 are
        // both full float4 streams, matching the factory signature even when a material only consumes xy.
        vertices[index] = new ParticleBeamTrailVertex(
            position,
            position - direction,
            direction,
            new Vector4(u, v, 0, 0),
            rotation,
            color == Vector4.Zero ? Vector4.One : color,
            dynamicParameter);
    }

    private static List<RibbonSegment> BuildBeamSegments(VfxEmitterState emitter, Matrix4x4 previewTransform)
    {
        var segments = new List<RibbonSegment>();
        VfxBeamDefinition beam = emitter.Definition.Beam;
        if (beam is null)
        {
            return segments;
        }

        Matrix4x4 transform = previewTransform == default ? Matrix4x4.Identity : previewTransform;
        float transformScale = GetMaximumScale(transform);
        IReadOnlyList<VfxParticle> particles = emitter.Particles.Count > 0
            ? emitter.Particles
            : [new VfxParticle { Color = Vector4.One, Size = Vector3.One, Random = 0.5f }];
        foreach (VfxParticle particle in particles)
        {
            Vector3 source = Vector3.Transform(beam.Source.Evaluate(particle.RelativeTime, particle.Random), transform);
            Vector3 target = Vector3.Transform(beam.Target.Evaluate(particle.RelativeTime, particle.Random), transform);
            if (Vector3.DistanceSquared(source, target) < 0.0001f)
            {
                float distance = Math.Max(1, beam.Distance.Evaluate(particle.RelativeTime, particle.Random));
                target = source + Vector3.TransformNormal(Vector3.UnitZ * distance, transform);
            }
            int subdivisionCount = Math.Max(1, beam.InterpolationPoints + 1);
            float width = GetRibbonWidth(particle, emitter.Definition) * transformScale;
            for (int subdivision = 0; subdivision < subdivisionCount; subdivision++)
            {
                float startU = subdivision / (float)subdivisionCount;
                float endU = (subdivision + 1) / (float)subdivisionCount;
                segments.Add(new RibbonSegment(
                    Vector3.Lerp(source, target, startU),
                    Vector3.Lerp(source, target, endU),
                    width,
                    width,
                    particle.Color,
                    particle.Color,
                    particle.DynamicParameter,
                    particle.Rotation,
                    startU,
                    endU));
            }
        }
        return segments;
    }

    private static List<RibbonSegment> BuildTrailSegments(VfxEmitterState emitter, Matrix4x4 previewTransform)
    {
        var segments = new List<RibbonSegment>();
        if (emitter.Particles.Count < 2)
        {
            return segments;
        }

        Matrix4x4 transform = previewTransform == default ? Matrix4x4.Identity : previewTransform;
        float transformScale = GetMaximumScale(transform);
        VfxParticle[] ordered = emitter.Particles.OrderByDescending(particle => particle.Age).ToArray();
        for (int index = 1; index < ordered.Length; index++)
        {
            VfxParticle startParticle = ordered[index - 1];
            VfxParticle endParticle = ordered[index];
            float startU = (index - 1) / (float)(ordered.Length - 1);
            float endU = index / (float)(ordered.Length - 1);
            segments.Add(new RibbonSegment(
                Vector3.Transform(startParticle.Position + startParticle.OrbitOffset, transform),
                Vector3.Transform(endParticle.Position + endParticle.OrbitOffset, transform),
                GetRibbonWidth(startParticle, emitter.Definition) * transformScale,
                GetRibbonWidth(endParticle, emitter.Definition) * transformScale,
                startParticle.Color,
                endParticle.Color,
                Vector4.Lerp(startParticle.DynamicParameter, endParticle.DynamicParameter, 0.5f),
                (startParticle.Rotation + endParticle.Rotation) * 0.5f,
                startU,
                endU));
        }
        return segments;
    }

    private static float GetRibbonWidth(VfxParticle particle, VfxEmitterDefinition emitter)
    {
        float width = MathF.Max(
            MathF.Abs(particle.Size.X * emitter.SourceAspect.X),
            MathF.Abs(particle.Size.Y * emitter.SourceAspect.Y));
        return MathF.Max(0.01f, width);
    }

    private static float GetMaximumScale(Matrix4x4 transform)
        => MathF.Max(
            Vector3.TransformNormal(Vector3.UnitX, transform).Length(),
            MathF.Max(
                Vector3.TransformNormal(Vector3.UnitY, transform).Length(),
                Vector3.TransformNormal(Vector3.UnitZ, transform).Length()));

    private static void WriteParticleVertex(
        List<ParticleVertex> vertices,
        int index,
        Vector3 position,
        Vector3 oldPosition,
        Vector3 size,
        VfxParticle particle,
        Vector2 corner,
        Vector2 currentUV,
        Vector2 nextUV,
        float interpolation,
        SpriteResources resources)
    {
        // Non-SubUV factories use TEXCOORD0 both as the sampling coordinate and the centered expansion corner.
        // SubUV factories use TEXCOORD0.xy/zw for current/next frame UVs and TEXCOORD2.zw for expansion.
        Vector4 textureCoordinates = resources.UsesSubUV
            ? new Vector4(currentUV.X, currentUV.Y, nextUV.X, nextUV.Y)
            : new Vector4(corner, 0, 0);
        Vector4 subUVData = resources.UsesSubUV
            ? new Vector4(interpolation, 0, corner.X, corner.Y)
            : Vector4.Zero;
        Vector4 dynamicParameter = resources.UsesDynamicParameter ? particle.DynamicParameter : Vector4.Zero;
        vertices[index] = new ParticleVertex(
            position,
            oldPosition,
            size,
            particle.Rotation,
            textureCoordinates,
            particle.Color,
            subUVData,
            dynamicParameter);
    }

    private static void RenderNativeMaterial<TVertex>(
        MeshRenderContext context,
        MaterialRenderProxy material,
        Mesh<TVertex> mesh,
        RenderTargetBlendDescription blendDescription,
        bool depthTest,
        bool depthWrite,
        int indexCount)
        where TVertex : IVertexBase
    {
        LEEffect effect = context.LEEffect;
        PixelShader pixelShader = context.GetCachedNativePixelShader(
            material.UnrealPixelShader.Guid,
            material.UnrealPixelShader.ShaderByteCode);
        (VertexShader vertexShader, InputLayout inputLayout) = context.GetCachedVertexShader<TVertex>(
            material.UnrealVertexShader.Guid,
            material.UnrealVertexShader.ShaderByteCode);
        effect.PrepDraw(
            context.ImmediateContext,
            vertexShader,
            pixelShader,
            inputLayout,
            context.GetCachedBlendState(blendDescription));

        SceneCamera camera = context.Camera;
        var vertexConstants = new LEVSConstants
        {
            ViewProjectionMatrix = camera.ViewMatrix * camera.ProjectionMatrix,
            CameraPosition = new Vector4(camera.Position, 1),
            PreViewTranslation = Vector4.Zero
        };
        float depthMultiplier = camera.ProjectionMatrix[2, 2];
        float depthAddition = camera.ProjectionMatrix[3, 2];
        var pixelConstants = new LEPSConstants
        {
            ScreenPositionScaleBias = new Vector4(
                0.5f,
                -0.5f,
                (context.Height / 2f + 0.5f) / context.Height,
                (context.Width / 2f + 0.5f) / context.Width),
            MinZ_MaxZRatio = new Vector4(
                depthAddition,
                depthMultiplier,
                1f / depthAddition,
                depthMultiplier / depthAddition),
            DynamicScale = Vector4.One
        };

        material.UpdateShaderParams(
            effect.VertexShaderConstantBuffer,
            effect.PixelShaderConstantBuffer,
            context,
            mesh);
        context.ImmediateContext.OutputMerger.SetDepthStencilState(
            (context as VfxPreviewRenderContext)?.GetVfxDepthState(depthTest, depthWrite));
        effect.RenderObject(
            context.ImmediateContext,
            vertexConstants,
            pixelConstants,
            mesh,
            0,
            indexCount);
        context.ImmediateContext.OutputMerger.SetDepthStencilState(null);
    }

    private static Vector3 GetParticleSize(VfxParticle particle, VfxEmitterDefinition emitter)
    {
        float width = MathF.Abs(particle.Size.X * emitter.SourceAspect.X);
        float height = MathF.Abs(particle.Size.Y * emitter.SourceAspect.Y);
        if (emitter.ScreenAlignment == VfxScreenAlignment.Square)
        {
            width = height = MathF.Max(width, height);
        }
        return new Vector3(width, height, MathF.Abs(particle.Size.Z));
    }

    private static void GetFrameData(
        VfxEmitterDefinition emitter,
        float subImageIndex,
        out Vector2 currentMinimum,
        out Vector2 currentMaximum,
        out Vector2 nextMinimum,
        out Vector2 nextMaximum,
        out float interpolation)
    {
        int columns = Math.Max(1, emitter.SubImagesHorizontal);
        int rows = Math.Max(1, emitter.SubImagesVertical);
        int frameCount = columns * rows;
        float clampedIndex = Math.Clamp(subImageIndex, 0, Math.Max(0, frameCount - 1));
        int currentFrame = Math.Clamp((int)MathF.Floor(clampedIndex), 0, frameCount - 1);
        int nextFrame = Math.Min(currentFrame + 1, frameCount - 1);
        interpolation = emitter.SubUVInterpolation is VfxSubUVInterpolation.LinearBlend or VfxSubUVInterpolation.RandomBlend
            ? clampedIndex - currentFrame
            : 0;
        GetFrameUV(columns, rows, currentFrame, out currentMinimum, out currentMaximum);
        GetFrameUV(columns, rows, nextFrame, out nextMinimum, out nextMaximum);
    }

    private static void GetFrameUV(int columns, int rows, int frame, out Vector2 minimum, out Vector2 maximum)
    {
        Vector2 scale = new(1f / columns, 1f / rows);
        minimum = new Vector2((frame % columns) * scale.X, (frame / columns) * scale.Y);
        maximum = minimum + scale;
    }

    private static void ConfigureFactoryConstants(
        ParticleVertexFactoryRenderParameters values,
        VfxEmitterDefinition emitter)
    {
        values.ScreenAlignment = emitter.ScreenAlignment switch
        {
            VfxScreenAlignment.Rectangle => 1,
            VfxScreenAlignment.Velocity => 2,
            VfxScreenAlignment.TypeSpecific => 3,
            _ => 0
        };
        values.NormalsType = emitter.NormalsMode switch
        {
            VfxEmitterNormalsMode.Spherical => 1,
            VfxEmitterNormalsMode.Cylindrical => 2,
            _ => 0
        };
        values.NormalsSphereCenter = new Vector4(emitter.NormalsSphereCenter, 1);
        Vector3 cylinderDirection = emitter.NormalsCylinderDirection.LengthSquared() > 0.000001f
            ? Vector3.Normalize(emitter.NormalsCylinderDirection)
            : Vector3.UnitZ;
        values.NormalsCylinderUnitDirection = new Vector4(cylinderDirection, 0);

        Vector3 lockedAxis = GetLockedAxis(emitter.AxisLock);
        if (lockedAxis == Vector3.Zero)
        {
            values.AxisRotationVectorSourceIndex = 0;
            values.AxisRotationVectors = default;
            values.ParticleUpRightResultScalars = emitter.ScreenAlignment == VfxScreenAlignment.Velocity
                ? Vector3.UnitY
                : Vector3.UnitX;
            return;
        }

        Fixed2<Vector4> axisVectors = default;
        axisVectors[0] = new Vector4(lockedAxis, 1);
        axisVectors[1] = new Vector4(lockedAxis, 1);
        values.AxisRotationVectorSourceIndex = 0;
        values.AxisRotationVectors = axisVectors;
        values.ParticleUpRightResultScalars = Vector3.UnitZ;
    }

    private static Vector3 GetLockedAxis(VfxAxisLock axisLock) => axisLock switch
    {
        VfxAxisLock.PositiveX or VfxAxisLock.RotateX => Vector3.UnitX,
        VfxAxisLock.NegativeX => -Vector3.UnitX,
        VfxAxisLock.PositiveY or VfxAxisLock.RotateY => Vector3.UnitY,
        VfxAxisLock.NegativeY => -Vector3.UnitY,
        VfxAxisLock.PositiveZ or VfxAxisLock.RotateZ => Vector3.UnitZ,
        VfxAxisLock.NegativeZ => -Vector3.UnitZ,
        _ => Vector3.Zero
    };

    private static bool UsesSubUV(VfxEmitterDefinition emitter)
        => emitter.SubImagesHorizontal > 1
           || emitter.SubImagesVertical > 1
           || emitter.SubUVInterpolation != VfxSubUVInterpolation.None;

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
        resources.Mesh.Dispose();
        resources.Mesh = CreateSpriteMesh(context, capacity);
        resources.ParticleCapacity = capacity;
    }

    private static Mesh<ParticleVertex> CreateSpriteMesh(MeshRenderContext context, int particleCapacity)
    {
        var triangles = new List<Triangle>(particleCapacity * 2);
        var vertices = new List<ParticleVertex>(particleCapacity * 4);
        var placeholder = new ParticleVertex(
            Vector3.Zero, Vector3.Zero, Vector3.One, 0,
            Vector4.Zero, Vector4.One, Vector4.Zero, Vector4.Zero);
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
        return new Mesh<ParticleVertex>(context.Device, triangles, vertices, isDynamic: true);
    }

    private static void EnsureBeamTrailCapacity(MeshRenderContext context, BeamTrailResources resources, int segmentCount)
    {
        if (segmentCount <= resources.SegmentCapacity)
        {
            return;
        }
        int capacity = resources.SegmentCapacity;
        while (capacity < segmentCount)
        {
            capacity *= 2;
        }
        resources.Mesh.Dispose();
        resources.Mesh = CreateBeamTrailMesh(context, capacity);
        resources.SegmentCapacity = capacity;
    }

    private static Mesh<ParticleBeamTrailVertex> CreateBeamTrailMesh(MeshRenderContext context, int segmentCapacity)
    {
        var triangles = new List<Triangle>(segmentCapacity * 2);
        var vertices = new List<ParticleBeamTrailVertex>(segmentCapacity * 4);
        var placeholder = new ParticleBeamTrailVertex(
            Vector3.Zero, -Vector3.UnitZ, Vector3.UnitZ, Vector4.Zero, 0, Vector4.One);
        for (int segmentIndex = 0; segmentIndex < segmentCapacity; segmentIndex++)
        {
            uint vertexStart = (uint)(segmentIndex * 4);
            vertices.Add(placeholder);
            vertices.Add(placeholder);
            vertices.Add(placeholder);
            vertices.Add(placeholder);
            triangles.Add(new Triangle(vertexStart, vertexStart + 1, vertexStart + 2));
            triangles.Add(new Triangle(vertexStart, vertexStart + 2, vertexStart + 3));
        }
        return new Mesh<ParticleBeamTrailVertex>(context.Device, triangles, vertices, isDynamic: true);
    }

    private static RenderTargetBlendDescription CreateBlendDescription(EBlendMode blendMode) => blendMode switch
    {
        EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked => DisabledBlend(),
        EBlendMode.BLEND_Translucent or EBlendMode.BLEND_SoftMasked => EnabledBlend(
            BlendOption.SourceAlpha, BlendOption.InverseSourceAlpha,
            BlendOption.SourceAlphaSaturate, BlendOption.InverseSourceAlpha),
        EBlendMode.BLEND_Additive => EnabledBlend(
            BlendOption.One, BlendOption.One, BlendOption.Zero, BlendOption.One),
        EBlendMode.BLEND_Modulate => EnabledBlend(
            BlendOption.DestinationColor, BlendOption.Zero, BlendOption.Zero, BlendOption.One),
        EBlendMode.BLEND_AlphaComposite => EnabledBlend(
            BlendOption.One, BlendOption.InverseSourceAlpha, BlendOption.One, BlendOption.InverseSourceAlpha),
        _ => DisabledBlend()
    };

    private static RenderTargetBlendDescription DisabledBlend() => new()
    {
        RenderTargetWriteMask = ColorWriteMaskFlags.All,
        BlendOperation = BlendOperation.Add,
        AlphaBlendOperation = BlendOperation.Add,
        SourceBlend = BlendOption.One,
        DestinationBlend = BlendOption.Zero,
        SourceAlphaBlend = BlendOption.One,
        DestinationAlphaBlend = BlendOption.Zero,
        IsBlendEnabled = false
    };

    private static RenderTargetBlendDescription EnabledBlend(
        BlendOption source,
        BlendOption destination,
        BlendOption sourceAlpha,
        BlendOption destinationAlpha) => new()
    {
        RenderTargetWriteMask = ColorWriteMaskFlags.All,
        BlendOperation = BlendOperation.Add,
        AlphaBlendOperation = BlendOperation.Add,
        SourceBlend = source,
        DestinationBlend = destination,
        SourceAlphaBlend = sourceAlpha,
        DestinationAlphaBlend = destinationAlpha,
        IsBlendEnabled = true
    };

    public void Clear()
    {
        foreach (SpriteResources resources in spriteEmitters.Values)
        {
            resources.Dispose();
        }
        spriteEmitters.Clear();
        foreach (BeamTrailResources resources in beamTrailEmitters.Values)
        {
            resources.Dispose();
        }
        beamTrailEmitters.Clear();
    }

    public void Dispose() => Clear();
}
