using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

public readonly record struct VfxBillboardBasis(Vector3 Right, Vector3 Up, Vector3 Normal);

public static class VfxBillboardMath
{
    public static VfxBillboardBasis CreateBasis(
        Vector3 cameraRight,
        Vector3 cameraUp,
        Vector3 cameraForward,
        Vector3 velocity,
        VfxScreenAlignment alignment,
        VfxAxisLock axisLock,
        float rotation)
    {
        Vector3 normal = SafeNormalize(-cameraForward, Vector3.UnitX);
        Vector3 right = SafeNormalize(cameraRight, Vector3.UnitY);
        Vector3 up = SafeNormalize(cameraUp, Vector3.UnitZ);

        Vector3 lockedAxis = GetLockedAxis(axisLock);
        if (lockedAxis != Vector3.Zero)
        {
            up = lockedAxis;
            right = SafeNormalize(Vector3.Cross(up, normal), cameraRight);
            normal = SafeNormalize(Vector3.Cross(right, up), normal);
        }
        else if (alignment == VfxScreenAlignment.Velocity && velocity.LengthSquared() > 0.000001f)
        {
            up = velocity - (Vector3.Dot(velocity, normal) * normal);
            up = SafeNormalize(up, cameraUp);
            right = SafeNormalize(Vector3.Cross(up, normal), cameraRight);
        }

        float sin = MathF.Sin(rotation);
        float cos = MathF.Cos(rotation);
        Vector3 rotatedRight = (right * cos) + (up * sin);
        Vector3 rotatedUp = (-right * sin) + (up * cos);
        return new VfxBillboardBasis(rotatedRight, rotatedUp, normal);
    }

    public static void CreateQuad(
        in VfxParticle particle,
        VfxEmitterDefinition emitter,
        in VfxBillboardBasis basis,
        Span<Vector3> corners)
    {
        float width = MathF.Abs(particle.Size.X * emitter.SourceAspect.X);
        float height = MathF.Abs(particle.Size.Y * emitter.SourceAspect.Y);
        if (emitter.ScreenAlignment == VfxScreenAlignment.Square)
        {
            width = height = MathF.Max(width, height);
        }

        Vector3 center = particle.Position
            + (basis.Right * emitter.PivotOffset.X * width)
            + (basis.Up * emitter.PivotOffset.Y * height);
        Vector3 halfRight = basis.Right * width * 0.5f;
        Vector3 halfUp = basis.Up * height * 0.5f;
        corners[0] = center - halfRight + halfUp;
        corners[1] = center + halfRight + halfUp;
        corners[2] = center + halfRight - halfUp;
        corners[3] = center - halfRight - halfUp;
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

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
        => value.LengthSquared() < 0.000001f ? fallback : Vector3.Normalize(value);
}

public sealed class VfxBillboardRenderer : IDisposable
{
    internal const int MaterialUnlitFlag = 1 << 20;
    internal const int MaterialMaskedFlag = 1 << 21;
    internal const int OpacitySourceShift = 22;

    internal const string ParticleShader = """
struct VS_IN { float4 pos : POSITION0; float3 hitTestID : TANGENT0; float4 normal : NORMAL0; float4 color : COLOR1; float2 uv : TEXCOORD0; };
struct VS_OUT { float4 pos : SV_POSITION; float4 color : COLOR1; float3 normal : NORMAL; float3 worldPos : TEXCOORD1; float2 uv : TEXCOORD0; };
cbuffer constants { float4x4 projection; float4x4 view; float4x4 model; float3 HitTestID; int Flags; float4 AmbientColor; float4 LightPositionRadius[4]; float4 LightColorIntensity[4]; float4 LightDirectionInnerCone[4]; float4 LightOuterConeAndType[4]; };
Texture2D tex : register(t0); SamplerState samstate : register(s0);
VS_OUT VSMain(VS_IN input) { VS_OUT output = (VS_OUT)0; float4 worldPos = mul(float4(input.pos.xyz, 1), model); output.worldPos = worldPos.xyz; output.pos = mul(mul(worldPos, view), projection); output.color = input.color; output.normal = normalize(mul(float4(input.normal.xyz, 0), model).xyz); output.uv = input.uv; return output; }
float4 PSMain(VS_OUT input) : SV_TARGET0 { float4 textureSample = tex.Sample(samstate, input.uv); int opacitySource = (Flags >> 22) & 7; float textureOpacity = opacitySource == 1 ? dot(textureSample.rgb, float3(0.299, 0.587, 0.114)) : opacitySource == 2 ? textureSample.r : opacitySource == 3 ? textureSample.g : opacitySource == 4 ? textureSample.b : opacitySource == 5 ? 1 : textureSample.a; float alpha = saturate(textureOpacity * input.color.a); if ((Flags & (1 << 21)) != 0) clip(alpha - AmbientColor.a); float3 color = textureSample.rgb * input.color.rgb * AmbientColor.rgb; if (alpha <= 0.0001) color = 0; if ((Flags & (1 << 20)) != 0) return float4(color, alpha); float3 lighting = 0.2; [unroll] for (int i = 0; i < 4; i++) { float radius = LightPositionRadius[i].w; if (radius <= 0) continue; float3 delta = LightPositionRadius[i].xyz - input.worldPos; float distanceToLight = length(delta); if (distanceToLight >= radius) continue; float attenuation = saturate(1 - distanceToLight / radius); attenuation *= attenuation; lighting += LightColorIntensity[i].rgb * (LightColorIntensity[i].a * saturate(dot(normalize(input.normal), delta / max(distanceToLight, 0.0001))) * attenuation); } return float4(color * saturate(lighting), alpha); }
""";

    private readonly List<WorldVertex> vertices = [];
    private readonly List<int> indices = [];
    private GenericEffect<MeshRenderContext.WorldConstants> effect;

    public void CreateResources(MeshRenderContext context)
    {
        effect = new GenericEffect<MeshRenderContext.WorldConstants>(context.Device, ParticleShader);
    }

    public void Render(MeshRenderContext context, VfxEmitterState emitter, ShaderResourceView texture, BlendState blendState = null, DepthStencilState depthState = null, IReadOnlyList<VfxParticle> particleSource = null, Matrix4x4 previewTransform = default)
    {
        IReadOnlyList<VfxParticle> source = particleSource ?? emitter.Particles;
        if (source.Count == 0 || effect is null || texture is null)
        {
            return;
        }

        vertices.Clear();
        indices.Clear();
        var particles = new List<VfxParticle>(source);
        Matrix4x4 previewSpaceTransform = previewTransform == default ? Matrix4x4.Identity : previewTransform;
        if (particleSource is null)
        {
            SortParticles(particles, emitter.Definition.SortMode, context.Camera.Position, previewSpaceTransform);
        }

        // ParticleModuleRequired.MaxDrawCount clamps how many particles are actually rendered.
        if (emitter.Definition.UseMaxDrawCount && emitter.Definition.MaxDrawCount >= 0 && particles.Count > emitter.Definition.MaxDrawCount)
        {
            particles.RemoveRange(emitter.Definition.MaxDrawCount, particles.Count - emitter.Definition.MaxDrawCount);
        }

        using Texture2D textureResource = texture.Resource.QueryInterface<Texture2D>();
        int textureWidth = textureResource.Description.Width;
        int textureHeight = textureResource.Description.Height;
        Span<Vector3> corners = stackalloc Vector3[4];
        foreach (VfxParticle particle in particles)
        {
            VfxBillboardBasis basis = VfxBillboardMath.CreateBasis(
                context.Camera.CameraRight,
                context.Camera.CameraUp,
                context.Camera.CameraForward,
                particle.Velocity,
                emitter.Definition.ScreenAlignment,
                emitter.Definition.AxisLock,
                particle.Rotation);
            VfxParticle renderParticle = particle;
            renderParticle.Position += particle.OrbitOffset;
            renderParticle.Position = Vector3.Transform(renderParticle.Position, previewSpaceTransform);
            VfxBillboardMath.CreateQuad(renderParticle, emitter.Definition, basis, corners);

            int vertexStart = vertices.Count;
            Vector4 normal = new(basis.Normal, 0);
            GetSubUVs(emitter.Definition, particle.SubImageIndex, textureWidth, textureHeight, out Vector2 uvMinimum, out Vector2 uvMaximum);
            AddVertex(corners[0], normal, particle.Color, new Vector2(uvMinimum.X, uvMinimum.Y));
            AddVertex(corners[1], normal, particle.Color, new Vector2(uvMaximum.X, uvMinimum.Y));
            AddVertex(corners[2], normal, particle.Color, new Vector2(uvMaximum.X, uvMaximum.Y));
            AddVertex(corners[3], normal, particle.Color, new Vector2(uvMinimum.X, uvMaximum.Y));
            indices.Add(vertexStart);
            indices.Add(vertexStart + 1);
            indices.Add(vertexStart + 2);
            indices.Add(vertexStart);
            indices.Add(vertexStart + 2);
            indices.Add(vertexStart + 3);
        }

        MeshRenderContext.WorldConstants constants = context.GetWorldConstants(Matrix4x4.Identity);
        VfxParticleMaterialDefinition material = emitter.Definition.ParticleMaterial;
        if (material.IsUnlit)
        {
            constants.Flags |= (RenderContext.ShaderFlags)MaterialUnlitFlag;
        }
        if (material.BlendMode == VfxBlendMode.Masked)
        {
            constants.Flags |= (RenderContext.ShaderFlags)MaterialMaskedFlag;
        }
        constants.Flags |= (RenderContext.ShaderFlags)((int)material.OpacitySource << OpacitySourceShift);
        constants.AmbientColor = new Vector4(material.EmissiveTint.X, material.EmissiveTint.Y, material.EmissiveTint.Z, material.OpacityMaskClipValue);
        effect.PrepDraw(context.ImmediateContext, blendState ?? context.AlphaBlendState, constants);
        context.ImmediateContext.OutputMerger.SetDepthStencilState(depthState);
        context.ImmediateContext.PixelShader.SetShaderResource(0, texture);
        effect.PrepPrimitiveBuffers(context, [], System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
        effect.RenderPrimitives(context, PrimitiveTopology.TriangleList, 0, indices.Count, 0);
        context.ImmediateContext.PixelShader.SetShaderResource(0, null);
        context.ImmediateContext.OutputMerger.SetDepthStencilState(null);
    }

    /// <summary>
    /// Applies ParticleModuleRequired.SortMode. PSORTMODE_None keeps spawn order, the depth modes sort
    /// back-to-front, and the age modes order by remaining lifetime.
    /// </summary>
    public static void SortParticles(List<VfxParticle> particles, VfxSortMode sortMode, Vector3 cameraPosition, Matrix4x4 transform)
    {
        switch (sortMode)
        {
            case VfxSortMode.None:
                break;
            case VfxSortMode.AgeOldestFirst:
                particles.Sort((left, right) => right.Age.CompareTo(left.Age));
                break;
            case VfxSortMode.AgeNewestFirst:
                particles.Sort((left, right) => left.Age.CompareTo(right.Age));
                break;
            default:
                particles.Sort((left, right) => DistanceSquared(right, cameraPosition, transform)
                    .CompareTo(DistanceSquared(left, cameraPosition, transform)));
                break;
        }
    }

    public static float DistanceSquared(in VfxParticle particle, Vector3 cameraPosition, Matrix4x4 transform = default)
    {
        if (transform == default)
        {
            transform = Matrix4x4.Identity;
        }
        return Vector3.DistanceSquared(Vector3.Transform(particle.Position + particle.OrbitOffset, transform), cameraPosition);
    }

    public static void GetSubUVs(VfxEmitterDefinition emitter, float subImageIndex, out Vector2 minimum, out Vector2 maximum)
    {
        GetSubUVs(emitter, subImageIndex, 0, 0, out minimum, out maximum);
    }

    public static void GetSubUVs(VfxEmitterDefinition emitter, float subImageIndex, int textureWidth, int textureHeight, out Vector2 minimum, out Vector2 maximum)
    {
        int columns = Math.Max(1, emitter.SubImagesHorizontal);
        int rows = Math.Max(1, emitter.SubImagesVertical);
        int frameCount = columns * rows;
        int frame = Math.Clamp((int)MathF.Floor(subImageIndex), 0, frameCount - 1);
        int column = frame % columns;
        int row = frame / columns;
        Vector2 scale = new(1f / columns, 1f / rows);
        minimum = new Vector2(column * scale.X, row * scale.Y);
        maximum = minimum + scale;
        if ((columns > 1 || rows > 1) && textureWidth > 0 && textureHeight > 0)
        {
            Vector2 halfTexel = new(0.5f / Math.Max(1, textureWidth), 0.5f / Math.Max(1, textureHeight));
            minimum += halfTexel;
            maximum -= halfTexel;
        }
    }

    private void AddVertex(Vector3 position, Vector4 normal, Vector4 color, Vector2 uv)
    {
        var vertex = new WorldVertex(position, normal, uv) { Color = color };
        vertices.Add(vertex);
    }

    public void Dispose()
    {
        effect?.Dispose();
        effect = null;
    }
}
