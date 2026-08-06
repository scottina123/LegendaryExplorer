using System;
using System.Collections.Generic;
using System.Numerics;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using SharpDX;
using SharpDX.Direct3D11;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

internal static class ShaderParameterSetters
{
    public static void WriteValues<LightMapPolicy, DensityPolicy, TVertex>(this TBasePassVertexShader<LightMapPolicy, DensityPolicy> shader,
        Span<byte> buffer, MeshRenderContext context, Mesh<TVertex> mesh, MaterialRenderProxy mat)
        where LightMapPolicy : struct, IVertexParametersType where DensityPolicy : struct, IVertexShaderParametersType
        where TVertex : IVertexBase
    {
        switch (shader.VertexFactoryParameters.Parameters)
        {
            case FLocalVertexFactoryShaderParameters localVertexFactory:
                localVertexFactory.WriteValues(buffer, context, mesh, mat);
                break;
            case FParticleVertexFactoryShaderParameters particleVertexFactory:
                particleVertexFactory.WriteValues(buffer, context, mesh, mat);
                break;
            case FParticleBeamTrailVertexFactoryShaderParameters beamTrailVertexFactory:
                beamTrailVertexFactory.WriteValues(buffer, context, mesh, mat);
                break;
            case FParticleInstancedMeshVertexFactoryShaderParameters instancedMeshVertexFactory:
                instancedMeshVertexFactory.WriteValues(buffer, context, mesh, mat);
                break;
            default:
                throw new NotSupportedException($"{shader.VertexFactoryParameters.VertexFactoryType} is not supported by the renderer");
        }
        //TODO: LightMapPolicy params
        shader.HeightFogParameters.WriteValues(buffer, context, mesh, mat);
        shader.MaterialParameters.WriteValues(buffer, context, mesh, mat);
        //TODO: DensityPolicy params
    }
    public static void WriteValues<LightMapPolicy, TVertex>(this TBasePassPixelShader<LightMapPolicy> shader,
        Span<byte> buffer, MeshRenderContext context, Mesh<TVertex> mesh, MaterialRenderProxy mat)
        where LightMapPolicy : struct, IPixelParametersType
        where TVertex : IVertexBase
    {
        //TODO: LightMapPolicy params
        shader.MaterialParameters.WriteValues(buffer, context, mesh, mat);
        bool drawUnlit = mat.IsUnlit;
        bool skylight = !drawUnlit;
        buffer.WriteVal(shader.AmbientColorAndSkyFactor, drawUnlit ? new LinearColor(1, 1, 1, 0) : new LinearColor(0, 0, 0, 1));
        Vector3 upperSkyColor = Vector3.Zero;
        Vector3 lowerSkyColor = Vector3.Zero;
        if (skylight)
        {
            upperSkyColor = new Vector3(1, 1, 1);
            lowerSkyColor = new Vector3(1, 1, 1);
        }
        buffer.WriteVal(shader.UpperSkyColor, upperSkyColor);
        buffer.WriteVal(shader.LowerSkyColor, lowerSkyColor);
        buffer.WriteVal(shader.CharacterMask, 1f);
        buffer.WriteVal(shader.MotionBlurMask, 0f);
        if (shader.TranslucencyDepth.IsBound())
        {
            //no idea what this should be
            buffer.WriteVal(shader.TranslucencyDepth, Vector4.One);
        }
    }

    public static void WriteValues<TVertex>(this ref FMaterialVertexShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<TVertex> mesh, MaterialRenderProxy mat)
        where TVertex : IVertexBase
    {
        buffer.WriteVal(p.CameraWorldPosition, context.Camera.Position);
        buffer.WriteVal(p.ObjectWorldPositionAndRadius, new Vector4(mesh.TransformedBounds.Origin, mesh.TransformedBounds.SphereRadius));
        buffer.WriteVal(p.ObjectOrientation, mesh.LocalToWorld.GetAxis(2).Normal());
        buffer.WriteVal(p.WindDirectionAndSpeed, Vector4.Zero);
        buffer.WriteVal(p.FoliageImpulseDirection, Vector3.Zero);
        buffer.WriteVal(p.FoliageNormalizedRotationAxisAndAngle, Vector4.UnitZ);

        (List<Vector4> scalarParamValues, List<Vector4> vectorParamValues) = mat.GetCachedVertexParameters(context);
        foreach (TUniformParameter<FShaderParameter> scalarParam in p.UniformVertexScalarShaderParameters)
        {
            buffer.WriteVal(scalarParam.Param, scalarParamValues[scalarParam.Index]);
        }
        foreach (TUniformParameter<FShaderParameter> vectorParam in p.UniformVertexVectorShaderParameters)
        {
            buffer.WriteVal(vectorParam.Param, vectorParamValues[vectorParam.Index]);
        }
    }
    public static void WriteValues<TVertex>(this ref FMaterialPixelShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<TVertex> mesh, MaterialRenderProxy mat)
        where TVertex : IVertexBase
    {
        buffer.WriteVal(p.CameraWorldPosition, context.Camera.Position);
        buffer.WriteVal(p.ObjectWorldPositionAndRadius, new Vector4(mesh.TransformedBounds.Origin, mesh.TransformedBounds.SphereRadius));
        buffer.WriteVal(p.ObjectOrientation, mesh.LocalToWorld.GetAxis(2).Normal());
        buffer.WriteVal(p.WindDirectionAndSpeed, Vector4.Zero);
        buffer.WriteVal(p.FoliageImpulseDirection, Vector3.Zero);
        buffer.WriteVal(p.FoliageNormalizedRotationAxisAndAngle, Vector4.UnitZ);

        (List<Vector4> scalarParamValues, 
            List<Vector4> vectorParamValues, 
            List<PreviewTextureCache.TextureEntry> tex2dParamValues, 
            List<PreviewTextureCache.TextureEntry> cubeMapParamValues) = mat.GetCachedPixelParameters(context);

        foreach (TUniformParameter<FShaderParameter> scalarParam in p.UniformPixelScalarShaderParameters)
        {
            buffer.WriteVal(scalarParam.Param, scalarParamValues[scalarParam.Index]);
        }
        foreach (TUniformParameter<FShaderParameter> vectorParam in p.UniformPixelVectorShaderParameters)
        {
            buffer.WriteVal(vectorParam.Param, vectorParamValues[vectorParam.Index]);
        }
        foreach (TUniformParameter<FShaderResourceParameter> texParam in p.UniformPixel2DShaderResourceParameters)
        {
            PreviewTextureCache.TextureEntry texture = tex2dParamValues[texParam.Index];
            ShaderResourceView view = texture?.TextureView ?? context.WhiteTexView;
            context.ImmediateContext.PixelShader.SetShaderResource(texParam.Param.BaseIndex, view);
            context.ImmediateContext.PixelShader.SetSampler(texParam.Param.SamplerIndex, context.GetTextureSampler(texture));
        }
        foreach (TUniformParameter<FShaderResourceParameter> cubeParam in p.UniformPixelCubeShaderResourceParameters)
        {
            PreviewTextureCache.TextureEntry texture = cubeMapParamValues[cubeParam.Index];
            ShaderResourceView view = texture?.TextureView ?? context.WhiteTextureCubeView;
            context.ImmediateContext.PixelShader.SetShaderResource(cubeParam.Param.BaseIndex, view);
            context.ImmediateContext.PixelShader.SetSampler(cubeParam.Param.SamplerIndex, context.GetTextureSampler(texture));
        }

        SceneCamera camera = context.Camera;
        buffer.WriteVal(p.LocalToWorld, mesh.LocalToWorld);
        buffer.WriteVal(p.WorldToLocal, mesh.WorldToLocal);
        Matrix4x4 viewMatrix = camera.ViewMatrix;
        buffer.WriteVal(p.WorldToView, new Matrix3x3(viewMatrix.M11, viewMatrix.M12, viewMatrix.M13, viewMatrix.M21, viewMatrix.M22, viewMatrix.M23, viewMatrix.M31, viewMatrix.M32, viewMatrix.M33));
        Matrix4x4.Invert(viewMatrix, out Matrix4x4 inverseViewMatrix);
        Matrix4x4 projectionMatrix = camera.ProjectionMatrix;
        Matrix4x4.Invert(projectionMatrix, out Matrix4x4 inverseProjectionMatrix);
        buffer.WriteVal(p.InvViewProjection, inverseProjectionMatrix * inverseViewMatrix);
        buffer.WriteVal(p.ViewProjection, viewMatrix * projectionMatrix);

        p.SceneTextureParameters.WriteValues(buffer, context, mesh, mat);

        buffer.WriteVal(p.TwoSidedSign, 1f); //-1 if rendering backface?
        buffer.WriteVal(p.InvGamma, 1f / (1f /*GammaCorrection*/ ));
        buffer.WriteVal(p.DecalFarPlaneDistance, 65536f); //actual value is stored on the BioDecalComponent

        //these are used for ParticleSystem rendering
        buffer.WriteVal(p.ObjectPostProjectionPosition, Vector3.Zero);
        buffer.WriteVal(p.ObjectMacroUVScales, Vector4.Zero);
        buffer.WriteVal(p.ObjectNDCPosition, Vector3.Zero);
        buffer.WriteVal(p.OcclusionPercentage, 0f);

        const int isFading = 0;
        buffer.WriteVal(p.EnableScreenDoorFade, isFading);
        if (isFading > 0)
        {
            buffer.WriteVal(p.ScreenDoorFadeSettings, Vector4.Zero);
            buffer.WriteVal(p.ScreenDoorFadeSettings2, Vector4.Zero);
        }
        if (p.ScreenDoorNoiseTexture.IsBound())
        {
            context.ImmediateContext.PixelShader.SetShaderResource(p.ScreenDoorNoiseTexture.BaseIndex, null);
        }
        // Wrap lighting is not modeled by the preview; its cleared constants leave it disabled.
    }

    public static void WriteValues<TVertex>(this ref FSceneTextureShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<TVertex> mesh, MaterialRenderProxy mat)
        where TVertex : IVertexBase
    {
        if (p.SceneColorTexture.IsBound())
        {
            context.ImmediateContext.PixelShader.SetShaderResource(p.SceneColorTexture.BaseIndex, null);
        }
        if (p.SceneDepthTexture.IsBound())
        {
            // A null SRV samples as zero (the near plane). DepthBiasedAlpha then fades the entire particle out.
            // VFX preview contexts provide a neutral far-depth texture until the preview owns a sampleable copy
            // of its actual depth buffer; other native material previews retain their existing null binding.
            context.ImmediateContext.PixelShader.SetShaderResource(
                p.SceneDepthTexture.BaseIndex,
                context.PreviewSceneDepthTextureView);
        }

        if (p.ScreenPositionScaleBias.IsBound())
        {
            buffer.WriteVal(p.ScreenPositionScaleBias, new Vector4(1f / 2f, 1f / -2f, (context.Height / 2f + 0.5f) / context.Height, (context.Width / 2f + 0.5f) / context.Width));

        }
        if (p.MinZ_MaxZRatio.IsBound())
        {
            float depthMul = context.Camera.ProjectionMatrix[2, 2];
            float depthAdd = context.Camera.ProjectionMatrix[3, 2];
            if (false) //TODO: check if Z is inverted, if so this should be true
            {
                depthMul = 1f - depthMul;
                depthAdd = -depthAdd;
            }
            buffer.WriteVal(p.MinZ_MaxZRatio, new Vector4(depthAdd, depthMul, 1f / depthAdd, depthMul / depthAdd));
        }
    }

    public static void WriteValues<TVertex>(this ref FHeightFogVertexShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<TVertex> mesh, MaterialRenderProxy mat)
        where TVertex : IVertexBase
    {
        //these values disable fog
        buffer.WriteVal(p.FogExtinctionDistance, new Vector4(float.MaxValue));
        var fogInScatteringValue = new Fixed4<LinearColor>();
        fogInScatteringValue[0] = LinearColor.Black;
        fogInScatteringValue[1] = LinearColor.Black;
        fogInScatteringValue[2] = LinearColor.Black;
        fogInScatteringValue[3] = LinearColor.Black;
        buffer.WriteVal(p.FogInScattering, fogInScatteringValue);
        buffer.WriteVal(p.FogDistanceScale, Vector4.Zero);
        buffer.WriteVal(p.FogMinHeight, Vector4.Zero);
        buffer.WriteVal(p.FogMaxHeight, Vector4.Zero);
        buffer.WriteVal(p.FogStartDistance, Vector4.Zero);
    }

    public static void WriteValues<TVertex>(this FLocalVertexFactoryShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<TVertex> mesh, MaterialRenderProxy mat)
        where TVertex : IVertexBase
    {
        buffer.WriteVal(p.LocalToWorld, mesh.LocalToWorld);
        buffer.WriteVal(p.WorldToLocal, mesh.WorldToLocal);
        buffer.WriteVal(p.LocalToWorldRotDeterminantFlip, mesh.LocalToWorld.GetDeterminant() >= 0 ? 1f : -1f);
    }

    public static void WriteValues<TVertex>(this FParticleVertexFactoryShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<TVertex> mesh, MaterialRenderProxy mat)
        where TVertex : IVertexBase
    {
        ParticleVertexFactoryRenderParameters values = mat.ParticleFactoryParameters;
        buffer.WriteVal(p.CameraWorldPosition, new Vector4(context.Camera.Position, 1));
        buffer.WriteVal(p.CameraRight, new Vector4(context.Camera.CameraRight, 0));
        buffer.WriteVal(p.CameraUp, new Vector4(context.Camera.CameraUp, 0));
        buffer.WriteVal(p.ScreenAlignment, new Vector4(values.ScreenAlignment));
        buffer.WriteVal(p.LocalToWorld, mesh.LocalToWorld);
        buffer.WriteVal(p.AxisRotationVectorSourceIndex, values.AxisRotationVectorSourceIndex);
        buffer.WriteVal(p.AxisRotationVectors, values.AxisRotationVectors);
        buffer.WriteVal(p.ParticleUpRightResultScalars, values.ParticleUpRightResultScalars);
        buffer.WriteVal(p.NormalsType, values.NormalsType);
        buffer.WriteVal(p.NormalsSphereCenter, values.NormalsSphereCenter);
        buffer.WriteVal(p.NormalsCylinderUnitDirection, values.NormalsCylinderUnitDirection);
    }

    public static void WriteValues<TVertex>(this FParticleBeamTrailVertexFactoryShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<TVertex> mesh, MaterialRenderProxy mat)
        where TVertex : IVertexBase
    {
        buffer.WriteVal(p.CameraWorldPosition, new Vector4(context.Camera.Position, 1));
        buffer.WriteVal(p.CameraRight, new Vector4(context.Camera.CameraRight, 0));
        buffer.WriteVal(p.CameraUp, new Vector4(context.Camera.CameraUp, 0));
        buffer.WriteVal(p.ScreenAlignment, new Vector4(mat.ParticleFactoryParameters.ScreenAlignment));
        buffer.WriteVal(p.LocalToWorld, mesh.LocalToWorld);
    }

    public static void WriteValues<TVertex>(this FParticleInstancedMeshVertexFactoryShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<TVertex> mesh, MaterialRenderProxy mat)
        where TVertex : IVertexBase
    {
        float vertexCount = MathF.Max(1, mat.ParticleFactoryParameters.NumVerticesPerInstance);
        buffer.WriteVal(p.InvNumVerticesPerInstance, 1f / vertexCount);
        buffer.WriteVal(p.NumVerticesPerInstance, vertexCount);
        buffer.WriteVal(p.InstancedPreViewTranslation, mat.ParticleFactoryParameters.InstancedPreViewTranslation);
    }

    private static unsafe void WriteVal<T>(this Span<byte> buff, FShaderParameter param, T val) where T : unmanaged
    {
        if (!param.IsBound())
        {
            return;
        }
        //if (sizeof(T) != param.NumBytes 
        //    && !(typeof(T) == typeof(Matrix3x3) && param.NumBytes == 44) 
        //    && Debugger.IsAttached)
        //{
        //    Debugger.Break();
        //}
        int bytesToWrite = Math.Min(sizeof(T), param.NumBytes);
        val.AsBytes()[..bytesToWrite].CopyTo(buff[param.BaseIndex..]);
    }
}
