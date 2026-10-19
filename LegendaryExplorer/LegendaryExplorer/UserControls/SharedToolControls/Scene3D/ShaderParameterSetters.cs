using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using SharpDX;
using SharpDX.Direct3D11;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D
{
    internal static class ShaderParameterSetters
    {
        //for many of the params, we assume the mesh's LocalToWorld matrix is the Identity matrix
        //if ever we did something more complex than display one mesh at a time, this assumption would obviously no longer be valid

        public static void WriteBasePassVertexShaderValues(Shader shader, Span<byte> buffer,
            MeshRenderContext context, Mesh<LEVertex> mesh, MaterialRenderProxy material)
        {
            switch (shader)
            {
                case TBasePassVertexShader<FNullPolicy, FNullPolicy> noLightMapShader:
                    noLightMapShader.WriteValues(buffer, context, mesh, material);
                    break;
                case TBasePassVertexShader<FDirectionalLightPolicy.VertexParametersType, FNullPolicy> directionalShader:
                    directionalShader.WriteValues(buffer, context, mesh, material);
                    buffer.WriteVal(directionalShader.LightMapVertexParams.LightDirection,
                        new Vector4(GetPreviewLightDirection(context.Camera), 1));
                    break;
                case null:
                    break;
                default:
                    throw new NotSupportedException($"{shader.ShaderType} is not supported by the renderer");
            }
        }

        public static void WriteBasePassPixelShaderValues(Shader shader, Span<byte> buffer,
            MeshRenderContext context, Mesh<LEVertex> mesh, MaterialRenderProxy material)
        {
            switch (shader)
            {
                case TBasePassPixelShader<FNullPolicy> noLightMapShader:
                    noLightMapShader.WriteValues(buffer, context, mesh, material);
                    break;
                case TBasePassPixelShader<FDirectionalLightLightMapPolicy.PixelParametersType> directionalShader:
                    directionalShader.WriteValues(buffer, context, mesh, material);
                    buffer.WriteVal(directionalShader.PixelParams.LightColorAndFalloffExponent,
                        new Vector4(new Vector3(1f, 0.97f, 0.92f) * context.GameShaderLightScale, 0));
                    buffer.WriteVal(directionalShader.PixelParams.bReceiveDynamicShadows, 0);
                    break;
                case null:
                    break;
                default:
                    throw new NotSupportedException($"{shader.ShaderType} is not supported by the renderer");
            }
        }

        internal static Vector3 GetPreviewLightDirection(SceneCamera camera)
        {
            Matrix4x4.Invert(camera.ViewMatrix, out Matrix4x4 inverseView);
            // A key light above and to the left of the camera keeps skin normals and specular
            // textures visible while orbiting the preview. Translation does not affect a direction.
            return Vector3.Normalize(Vector3.TransformNormal(new Vector3(-1, 1, -1), inverseView));
        }

        public static void WriteValues<LightMapPolicy, DensityPolicy>(this TBasePassVertexShader<LightMapPolicy, DensityPolicy> shader, 
            Span<byte> buffer, MeshRenderContext context, Mesh<LEVertex> mesh, MaterialRenderProxy mat) 
            where LightMapPolicy : struct, IVertexParametersType where DensityPolicy : struct, IVertexShaderParametersType
        {
            if (shader.VertexFactoryParameters.Parameters is not FLocalVertexFactoryShaderParameters vertexFactoryParams)
            {
                throw new NotSupportedException($"{shader.VertexFactoryParameters.VertexFactoryType} is not supported by the renderer");
            }
            //TODO: LightMapPolicy params
            vertexFactoryParams.WriteValues(buffer, context, mesh, mat);
            shader.HeightFogParameters.WriteValues(buffer, context, mesh, mat);
            shader.MaterialParameters.WriteValues(buffer, context, mesh, mat);
            //TODO: DensityPolicy params
        }
        public static void WriteValues<LightMapPolicy>(this TBasePassPixelShader<LightMapPolicy> shader, 
            Span<byte> buffer, MeshRenderContext context, Mesh<LEVertex> mesh, MaterialRenderProxy mat) 
            where LightMapPolicy : struct, IPixelParametersType 
        {
            //TODO: LightMapPolicy params
            shader.MaterialParameters.WriteValues(buffer, context, mesh, mat);
            bool drawUnlit = mat.IsUnlit;
            bool skylight = !drawUnlit;
            float lightScale = context.GameShaderLightScale;
            buffer.WriteVal(shader.AmbientColorAndSkyFactor, drawUnlit
                ? new LinearColor(lightScale, lightScale, lightScale, 0)
                : new LinearColor(0, 0, 0, 1));
            Vector3 upperSkyColor = Vector3.Zero;
            Vector3 lowerSkyColor = Vector3.Zero;
            if (skylight)
            {
                upperSkyColor = (mat.UsesDirectionalPreview ? new Vector3(0.26f, 0.28f, 0.32f) : Vector3.One) * lightScale;
                lowerSkyColor = (mat.UsesDirectionalPreview ? new Vector3(0.08f, 0.07f, 0.06f) : Vector3.One) * lightScale;
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

        public static void WriteValues(this ref FMaterialVertexShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<LEVertex> mesh, MaterialRenderProxy mat)
        {
            buffer.WriteVal(p.CameraWorldPosition, context.Camera.WorldPosition);
            buffer.WriteVal(p.ObjectWorldPositionAndRadius, new Vector4(mesh.AABBCenter, mesh.AABBHalfSize.Length()));
            buffer.WriteVal(p.ObjectOrientation, Vector3.UnitY);
            buffer.WriteVal(p.WindDirectionAndSpeed, Vector4.Zero);
            buffer.WriteVal(p.FoliageImpulseDirection, Vector3.Zero);
            buffer.WriteVal(p.FoliageNormalizedRotationAxisAndAngle, Vector4.UnitZ);

            (List<Vector4> scalarParamValues, List<Vector4> vectorParamValues) = mat.GetCachedVertexParameters(context);
            foreach (TUniformParameter<FShaderParameter> scalarParam in p.UniformVertexScalarShaderParameters)
            {
                buffer.WriteScalarUniform(scalarParam, scalarParamValues, mat.Game);
            }
            foreach (TUniformParameter<FShaderParameter> vectorParam in p.UniformVertexVectorShaderParameters)
            {
                buffer.WriteVal(vectorParam.Param, vectorParamValues[vectorParam.Index]);
            }
        }
        public static void WriteValues(this ref FMaterialPixelShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<LEVertex> mesh, MaterialRenderProxy mat)
        {
            SceneCamera camera = context.Camera;
            buffer.WriteVal(p.CameraWorldPosition, camera.WorldPosition);
            buffer.WriteVal(p.ObjectWorldPositionAndRadius, new Vector4(mesh.AABBCenter, mesh.AABBHalfSize.Length()));
            buffer.WriteVal(p.ObjectOrientation, Vector3.UnitY);
            buffer.WriteVal(p.WindDirectionAndSpeed, Vector4.Zero);
            buffer.WriteVal(p.FoliageImpulseDirection, Vector3.Zero);
            buffer.WriteVal(p.FoliageNormalizedRotationAxisAndAngle, Vector4.UnitZ);

            (List<Vector4> scalarParamValues, 
                List<Vector4> vectorParamValues, 
                List<PreviewTextureCache.TextureEntry> tex2dParamValues, 
                List<PreviewTextureCache.TextureEntry> cubeMapParamValues) = mat.GetCachedPixelParameters(context);

            foreach (TUniformParameter<FShaderParameter> scalarParam in p.UniformPixelScalarShaderParameters)
            {
                buffer.WriteScalarUniform(scalarParam, scalarParamValues, mat.Game);
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


            buffer.WriteVal(p.LocalToWorld, Matrix4x4.Identity);
            buffer.WriteVal(p.WorldToLocal, Matrix4x4.Identity); //float3x3 params have rows padded to 4 floats, which a Matrix4x4 matches
            Matrix4x4 viewMatrix = camera.ViewMatrix;
            buffer.WriteVal(p.WorldToView, viewMatrix);
            Matrix4x4.Invert(viewMatrix, out Matrix4x4 inverseViewMatrix);
            Matrix4x4 projectionMatrix = camera.ProjectionMatrix;
            Matrix4x4.Invert(projectionMatrix, out Matrix4x4 inverseProjectionMatrix);
            buffer.WriteVal(p.InvViewProjection, inverseProjectionMatrix * inverseViewMatrix);
            buffer.WriteVal(p.ViewProjection, viewMatrix * projectionMatrix);

            p.SceneTextureParameters.WriteValues(buffer, context, mesh, mat);

            buffer.WriteVal(p.TwoSidedSign, 1f); //-1 if rendering backface?
            buffer.WriteVal(p.InvGamma, context.GameShaderInvGamma);
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
                //Debugger.Break();
                context.ImmediateContext.PixelShader.SetShaderResource(p.ScreenDoorNoiseTexture.BaseIndex, null);
            }
            if (p.WrapLightingParameters.IsBound())
            {
                buffer.WriteVal(p.WrapLightingParameters, new Vector4(0.33f, 0.05f, 0, 0));
            }
        }

        public static void WriteValues(this ref FSceneTextureShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<LEVertex> mesh, MaterialRenderProxy mat)
        {
            if (p.SceneColorTexture.IsBound())
            {
                //Debugger.Break();
                context.ImmediateContext.PixelShader.SetShaderResource(p.SceneColorTexture.BaseIndex, null);
            }
            if (p.SceneDepthTexture.IsBound())
            {
                //Debugger.Break();
                context.ImmediateContext.PixelShader.SetShaderResource(p.SceneDepthTexture.BaseIndex, null);
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

        public static void WriteValues(this ref FHeightFogVertexShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<LEVertex> mesh, MaterialRenderProxy mat)
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

        public static void WriteValues(this FLocalVertexFactoryShaderParameters p, Span<byte> buffer, MeshRenderContext context, Mesh<LEVertex> mesh, MaterialRenderProxy mat)
        {
            buffer.WriteVal(p.LocalToWorld, Matrix4x4.Identity);
            buffer.WriteVal(p.WorldToLocal, Matrix4x4.Identity); //float3x3 params have rows padded to 4 floats, which a Matrix4x4 matches
            buffer.WriteVal(p.LocalToWorldRotDeterminantFlip, 1f);
        }

    }
}
