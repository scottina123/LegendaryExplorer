using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;

namespace LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D
{
    //update the name strings too
    using VertexShaderType = TBasePassVertexShader<FNullPolicy, FNullPolicy>;
    using PixelShaderType = TBasePassPixelShader<FNullPolicy>;
    public class MaterialRenderProxy(ExportEntry export, PackageCache assetCache = null)
        : MaterialInstanceConstant(export, assetCache, true)
    {
        private const string VERTEX_SHADER_TYPE_NAME = "TBasePassVertexShaderFNoLightMapPolicyFNoDensityPolicy";
        private const string LIT_PIXEL_SHADER_TYPE_NAME = "TBasePassPixelShaderFNoLightMapPolicySkyLight";
        private const string UNLIT_PIXEL_SHADER_TYPE_NAME = "TBasePassPixelShaderFNoLightMapPolicyNoSkyLight";
        private const string HUMAN_LASH_MASTER_MATERIAL_NAME = "HMN_HED_LASH_Unlit_MASTER_MAT";
        private const string HUMAN_LASH_OPACITY_PARAMETER_NAME = "HED_Lash_Diff";

        public MEGame Game = export.Game;
        public EBlendMode BlendMode;
        public bool UseHairPass;
        public bool IsUnlit;
        public bool IsHumanLashMaterial;
        private readonly Dictionary<string, float> ScalarParameterValues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LinearColor> VectorParameterValues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> TextureParameterValues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IEntry> TextureParameterEntries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> DefaultTextureParameterNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (bool Exists, float Value)> PreviewScalarBaselines = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (bool Exists, LinearColor Value)> PreviewVectorBaselines = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (bool Exists, string Value)> PreviewTextureBaselines = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> Uniform2DTextureExpressions = [];
        public Dictionary<string, PreviewTextureCache.TextureEntry> TextureMap;
        private MaterialShaderMap ShaderMap;
        private uint CachedPixelFrameNumber = uint.MaxValue;
        private uint CachedVertexFrameNumber = uint.MaxValue;
        private readonly List<Vector4> CachedVertexScalarParameters = [];
        private readonly List<Vector4> CachedVertexVectorParameters = [];
        private readonly List<Vector4> CachedPixelScalarParameters = [];
        private readonly List<Vector4> CachedPixelVectorParameters = [];
        private readonly List<PreviewTextureCache.TextureEntry> CachedTexture2DParameters = [];
        private readonly List<PreviewTextureCache.TextureEntry> CachedCubeTextureParameters = [];

        public VertexShaderType VertexShader;
        public PixelShaderType PixelShader;

        /// <summary>
        /// Effective scalar parameters used by the preview, including inherited defaults and instance overrides.
        /// </summary>
        public IReadOnlyDictionary<string, float> ScalarParameters => ScalarParameterValues;

        /// <summary>
        /// Effective vector parameters used by the preview, including inherited defaults and instance overrides.
        /// </summary>
        public IReadOnlyDictionary<string, LinearColor> VectorParameters => VectorParameterValues;

        public void SetScalarParameter(string parameterName, float value)
        {
            PreviewScalarBaselines.TryAdd(parameterName, ScalarParameterValues.TryGetValue(parameterName, out float baseline)
                ? (true, baseline)
                : (false, default));
            ScalarParameterValues[parameterName] = value;
            CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;
        }

        public void SetVectorParameter(string parameterName, LinearColor value)
        {
            PreviewVectorBaselines.TryAdd(parameterName, VectorParameterValues.TryGetValue(parameterName, out LinearColor baseline)
                ? (true, baseline)
                : (false, default));
            VectorParameterValues[parameterName] = value;
            CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;
        }

        /// <summary>
        /// Replaces a texture parameter for the live preview. The caller owns the cached GPU texture.
        /// </summary>
        public void SetTextureParameter(string parameterName, string texturePath, PreviewTextureCache.TextureEntry texture)
        {
            PreviewTextureBaselines.TryAdd(parameterName, TextureParameterValues.TryGetValue(parameterName, out string baseline)
                ? (true, baseline)
                : (false, default));
            if (string.IsNullOrEmpty(texturePath) || texture is null)
            {
                TextureParameterValues.Remove(parameterName);
            }
            else
            {
                TextureMap[texturePath] = texture;
                TextureParameterValues[parameterName] = texturePath;
            }
            CachedPixelFrameNumber = uint.MaxValue;
        }

        /// <summary>
        /// Restores parameters changed through the live-preview setters to their material values.
        /// </summary>
        public void ResetPreviewParameterOverrides()
        {
            foreach ((string name, (bool exists, float value)) in PreviewScalarBaselines)
            {
                if (exists) ScalarParameterValues[name] = value;
                else ScalarParameterValues.Remove(name);
            }
            foreach ((string name, (bool exists, LinearColor value)) in PreviewVectorBaselines)
            {
                if (exists) VectorParameterValues[name] = value;
                else VectorParameterValues.Remove(name);
            }
            foreach ((string name, (bool exists, string value)) in PreviewTextureBaselines)
            {
                if (exists) TextureParameterValues[name] = value;
                else TextureParameterValues.Remove(name);
            }
            PreviewScalarBaselines.Clear();
            PreviewVectorBaselines.Clear();
            PreviewTextureBaselines.Clear();
            CachedPixelFrameNumber = CachedVertexFrameNumber = uint.MaxValue;
        }

        protected override void ReadBaseMaterial(ExportEntry mat, PackageCache assetCache, Material parsedMaterial)
        {
            base.ReadBaseMaterial(mat, assetCache, parsedMaterial);

            var props = mat.GetProperties(packageCache: assetCache);
            Enum.TryParse(props.GetProp<EnumProperty>("BlendMode")?.Value ?? "BLEND_Opaque", out BlendMode);

            //if the MIC had a StaticPermutationResource, this is already set
            if (Uniform2DTextureExpressions.IsEmpty())
            {
                foreach (int uIndex in parsedMaterial.SM3MaterialResource.UniformExpressionTextures)
                {
                    Uniform2DTextureExpressions.Add(mat.FileRef.GetEntry(uIndex)?.InstancedFullPath);
                }
            }

            UseHairPass = props.GetProp<BoolProperty>("bHairPass") is { Value: true };
            IsUnlit = props.GetProp<EnumProperty>("LightingModel") is {} lightingModelProp && lightingModelProp.Value == "MLM_Unlit";
            IsHumanLashMaterial = mat.ObjectName.Name.Equals(HUMAN_LASH_MASTER_MATERIAL_NAME, StringComparison.OrdinalIgnoreCase);

            var expressionsProp = props.GetProp<ArrayProperty<ObjectProperty>>("Expressions");
            if (expressionsProp is not null)
            {
                foreach (ObjectProperty expressionProp in expressionsProp)
                {
                    ExportEntry expressionExport = expressionProp.ResolveToExport(mat.FileRef, assetCache);
                    var expressionProps = expressionExport?.GetProperties(packageCache: assetCache);
                    if (expressionProps?.GetProp<NameProperty>("ParameterName") is {} paramNameProp)
                    {
                        //this will run after ReadMaterialInstanceConstant, so we don't want to overwrite any values specified there
                        Property defaultValue = expressionProps.GetProp<Property>("DefaultValue");
                        if (defaultValue is FloatProperty defaultFloatProp)
                        {
                            ScalarParameterValues.TryAdd(paramNameProp.Value.Instanced, defaultFloatProp.Value);
                        }
                        else if (defaultValue is StructProperty defaultVectorProp)
                        {
                            VectorParameterValues.TryAdd(paramNameProp.Value.Instanced, CommonStructs.GetLinearColor(defaultVectorProp));
                        }
                        else if (expressionProps.GetProp<ObjectProperty>("Texture") is {} textureProp)
                        {
                            if (mat.FileRef.GetEntry(textureProp.Value) is {} texEntry)
                            {
                                string parameterName = paramNameProp.Value.Instanced;
                                RegisterDefaultTextureParameter(texEntry, parameterName);
                                if (!TextureParameterValues.ContainsKey(parameterName))
                                {
                                    Textures.Add(texEntry);
                                    TextureParameterValues.Add(parameterName, texEntry.InstancedFullPath);
                                    TextureParameterEntries.TryAdd(parameterName, texEntry);
                                }
                            }
                        }
                    }
                }
            }

            //if the MIC had a StaticPermutationResource, this is already set
            if (ShaderMap is null)
            {
                LoadShaders(mat);
            }
        }

        private void LoadShaders(ExportEntry mat)
        {
            (ShaderMap, Shader[] shaders) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(mat, VERTEX_SHADER_TYPE_NAME, LIT_PIXEL_SHADER_TYPE_NAME, UNLIT_PIXEL_SHADER_TYPE_NAME);

            foreach (MaterialUniformExpression expression in ShaderMap.UniformVertexScalarExpressions
                         .Concat(ShaderMap.UniformVertexVectorExpressions)
                         .Concat(ShaderMap.UniformPixelScalarExpressions)
                         .Concat(ShaderMap.UniformPixelVectorExpressions))
            {
                AddUniformExpressionParameters(expression);
            }

            VertexShader = (VertexShaderType)shaders[0];
            PixelShader = (PixelShaderType)(shaders[1] ?? shaders[2]);
        }

        private void AddUniformExpressionParameters(MaterialUniformExpression expression)
        {
            switch (expression)
            {
                case MaterialUniformExpressionScalarParameter scalarParameter:
                    ScalarParameterValues.TryAdd(scalarParameter.ParameterName.Instanced, scalarParameter.DefaultValue);
                    break;
                case MaterialUniformExpressionVectorParameter vectorParameter:
                    VectorParameterValues.TryAdd(vectorParameter.ParameterName.Instanced, vectorParameter.DefaultValue);
                    break;
                case MaterialUniformExpressionUnaryOp unaryOperation:
                    AddUniformExpressionParameters(unaryOperation.X);
                    break;
                case MaterialUniformExpressionBinaryOp binaryOperation:
                    AddUniformExpressionParameters(binaryOperation.A);
                    AddUniformExpressionParameters(binaryOperation.B);
                    break;
                case MaterialUniformExpressionClamp clamp:
                    AddUniformExpressionParameters(clamp.Input);
                    AddUniformExpressionParameters(clamp.Min);
                    AddUniformExpressionParameters(clamp.Max);
                    break;
            }
        }

        protected override void ReadMaterialInstanceConstant(ExportEntry matInst, PropertyCollection props)
        {
            base.ReadMaterialInstanceConstant(matInst, props);
            if (props.GetProp<ArrayProperty<StructProperty>>("ScalarParameterValues") is { } scalarValues)
            {
                foreach (StructProperty scalarValue in scalarValues)
                {
                    if (scalarValue.GetProp<NameProperty>("ParameterName") is { } paramNameProp
                        && scalarValue.GetProp<FloatProperty>("ParameterValue") is { } valProp)
                    {
                        ScalarParameterValues.TryAdd(paramNameProp.Value.Instanced, valProp.Value);
                    }
                }
            }
            if (props.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues") is { } vectorValues)
            {
                foreach (StructProperty vectorValue in vectorValues)
                {
                    if (vectorValue.GetProp<NameProperty>("ParameterName") is { } paramNameProp
                        && vectorValue.GetProp<StructProperty>("ParameterValue") is { } valProp)
                    {
                        VectorParameterValues.TryAdd(paramNameProp.Value.Instanced, CommonStructs.GetLinearColor(valProp));
                    }
                }
            }
            if (props.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues") is { } textureValues)
            {
                foreach (StructProperty textureValue in textureValues)
                {
                    if (textureValue.GetProp<NameProperty>("ParameterName") is { } paramNameProp
                        && textureValue.GetProp<ObjectProperty>("ParameterValue") is { } valProp)
                    {
                        IEntry textureEntry = valProp.ResolveToEntry(matInst.FileRef);
                        TextureParameterValues.TryAdd(paramNameProp.Value.Instanced, textureEntry?.InstancedFullPath);
                        if (textureEntry is not null)
                        {
                            TextureParameterEntries.TryAdd(paramNameProp.Value.Instanced, textureEntry);
                        }
                    }
                }
            }

            if (props.GetProp<BoolProperty>("bHasStaticPermutationResource") is { Value: true }
                && ObjectBinary.From(matInst) is MaterialInstance binary)
            {
                foreach (int uIndex in binary.SM3StaticPermutationResource.UniformExpressionTextures)
                {
                    Uniform2DTextureExpressions.Add(matInst.FileRef.GetEntry(uIndex)?.InstancedFullPath);
                }
                LoadShaders(matInst);
            }
        }

        public void UpdateShaderParams(Span<byte> vertexConstantBuffer, Span<byte> pixelConstantBuffer, MeshRenderContext context, Mesh<LEVertex> mesh)
        {
            VertexShader?.WriteValues(vertexConstantBuffer, context, mesh, this);
            PixelShader?.WriteValues(pixelConstantBuffer, context, mesh, this);
        }

        public (List<Vector4> scalar, List<Vector4> vector) GetCachedVertexParameters(MeshRenderContext context)
        {
            UpdateUniformVertexParameters(context);
            return (CachedVertexScalarParameters, CachedVertexVectorParameters);
        }

        public (List<Vector4> scalar, List<Vector4> vector, 
            List<PreviewTextureCache.TextureEntry> tex2d, List<PreviewTextureCache.TextureEntry> cube)
            GetCachedPixelParameters(MeshRenderContext context)
        {
            UpdateUniformPixelParameters(context);
            return (CachedPixelScalarParameters, CachedPixelVectorParameters, CachedTexture2DParameters, CachedCubeTextureParameters);
        }

        private void UpdateUniformVertexParameters(MeshRenderContext context)
        {
            if (CachedVertexFrameNumber == context.NumFrames) return;
            CachedVertexFrameNumber = context.NumFrames;
            CachedVertexScalarParameters.Clear();
            CachedVertexVectorParameters.Clear();

            var uniformContext = new UniformExpressionRenderContext(
                ScalarParameterValues, VectorParameterValues, 
                context.Time, context.Time, GetFlipBookTextureOffset);

            UpdateExpressions(uniformContext,
                ShaderMap.UniformVertexVectorExpressions, ShaderMap.UniformVertexScalarExpressions,
                CachedVertexScalarParameters, CachedVertexVectorParameters);
        }

        private void UpdateUniformPixelParameters(MeshRenderContext context)
        {
            if (CachedPixelFrameNumber == context.NumFrames) return;
            CachedPixelFrameNumber = context.NumFrames;
            CachedPixelScalarParameters.Clear();
            CachedPixelVectorParameters.Clear();
            CachedTexture2DParameters.Clear();
            CachedCubeTextureParameters.Clear();

            var uniformContext = new UniformExpressionRenderContext(
                ScalarParameterValues, VectorParameterValues, 
                context.Time, context.Time, GetFlipBookTextureOffset);

            UpdateExpressions(uniformContext,
                ShaderMap.UniformPixelVectorExpressions, ShaderMap.UniformPixelScalarExpressions,
                CachedPixelScalarParameters, CachedPixelVectorParameters);

            UpdateTextureExpressions(ShaderMap.Uniform2DTextureExpressions, CachedTexture2DParameters, context);
            UpdateTextureExpressions(ShaderMap.UniformCubeTextureExpressions, CachedCubeTextureParameters, context);
        }

        private LinearColor GetFlipBookTextureOffset(UniformExpressionRenderContext context, int texIndex)
        {
            if ((uint)texIndex < Uniform2DTextureExpressions.Count 
                && Uniform2DTextureExpressions[texIndex] is { } texifp
                && TextureMap.TryGetValue(texifp, out var texture)
                && texture is PreviewTextureCache.FlipBookTextureEntry flipBookTexture)
            {
                return flipBookTexture.GetTextureOffset(context);
            }
            return LinearColor.Black;
        }

        private void UpdateTextureExpressions(MaterialUniformExpressionTexture[] textureExpressions,
            List<PreviewTextureCache.TextureEntry> textureCache, MeshRenderContext context)
        {
            foreach (MaterialUniformExpressionTexture texExpression in textureExpressions)
            {
                PreviewTextureCache.TextureEntry texture = null;
                // The human lash master is a translucent black shell whose opacity comes entirely
                // from HED_Lash_Diff. Its cooked shader exposes that texture as a plain uniform
                // expression, so always honor the effective child-MIC parameter instead of falling
                // back to the master's male lash texture (or the renderer's opaque white texture).
                if (IsHumanLashMaterial && TextureParameterValues.ContainsKey(HUMAN_LASH_OPACITY_PARAMETER_NAME))
                {
                    textureCache.Add(ResolveTextureParameter(HUMAN_LASH_OPACITY_PARAMETER_NAME, context));
                    continue;
                }
                switch (texExpression)
                {
                    case MaterialUniformExpressionTextureParameter texParamExpression:
                        texture = ResolveTextureParameter(texParamExpression.ParameterName.Instanced, context);
                        break;
                    default:
                        if ((uint)texExpression.TextureIndex < Uniform2DTextureExpressions.Count
                            && Uniform2DTextureExpressions[texExpression.TextureIndex] is {} texifp)
                        {
                            texture = ResolveDefaultTextureOverride(texifp, context);
                            texture ??= TextureMap.GetValueOrDefault(texifp);
                        }
                        break;
                }
                textureCache.Add(texture);
            }
        }

        public bool HasRequiredTextures(MeshRenderContext context)
        {
            if (!IsHumanLashMaterial)
            {
                return true;
            }

            (_, _, List<PreviewTextureCache.TextureEntry> textures, _) = GetCachedPixelParameters(context);
            return textures.Count > 0 && textures.All(texture => texture is not null);
        }

        private void RegisterDefaultTextureParameter(IEntry textureEntry, string parameterName)
        {
            foreach (string texturePath in new[] { textureEntry.FullPath, textureEntry.InstancedFullPath })
            {
                if (string.IsNullOrEmpty(texturePath)) continue;
                if (!DefaultTextureParameterNames.TryGetValue(texturePath, out HashSet<string> parameterNames))
                {
                    parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    DefaultTextureParameterNames.Add(texturePath, parameterNames);
                }
                parameterNames.Add(parameterName);
            }
        }

        private PreviewTextureCache.TextureEntry ResolveDefaultTextureOverride(string defaultTexturePath, MeshRenderContext context)
        {
            if (!DefaultTextureParameterNames.TryGetValue(defaultTexturePath, out HashSet<string> parameterNames))
            {
                return null;
            }

            PreviewTextureCache.TextureEntry selectedTexture = null;
            foreach (string parameterName in parameterNames)
            {
                if (!TextureParameterValues.TryGetValue(parameterName, out string overridePath)
                    || string.IsNullOrEmpty(overridePath)
                    || string.Equals(overridePath, defaultTexturePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                PreviewTextureCache.TextureEntry candidate = ResolveTextureParameter(parameterName, context);
                if (candidate is null)
                {
                    continue;
                }
                if (selectedTexture is not null && !ReferenceEquals(selectedTexture, candidate))
                {
                    // A shared default texture is overridden by more than one parameter. There is
                    // no safe way to choose for a non-parameter uniform expression.
                    return null;
                }
                selectedTexture = candidate;
            }
            return selectedTexture;
        }

        private PreviewTextureCache.TextureEntry ResolveTextureParameter(string parameterName, MeshRenderContext context)
        {
            if (!TextureParameterValues.TryGetValue(parameterName, out string texturePath)
                || string.IsNullOrEmpty(texturePath))
            {
                return null;
            }

            if (TextureMap.TryGetValue(texturePath, out PreviewTextureCache.TextureEntry texture))
            {
                return texture;
            }
            if (!TextureParameterEntries.TryGetValue(parameterName, out IEntry textureEntry)
                || (!string.Equals(texturePath, textureEntry.InstancedFullPath, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(texturePath, textureEntry.FullPath, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            ExportEntry textureExport = textureEntry as ExportEntry;
            if (textureEntry is ImportEntry textureImport)
            {
                textureExport = EntryImporter.ResolveImport(textureImport, assetCache);
            }
            if (textureExport is null)
            {
                return null;
            }

            texture = context.TextureCache.LoadTexture(textureExport);
            if (texture is null)
            {
                return null;
            }
            TextureMap[textureEntry.FullPath] = texture;
            TextureMap[textureEntry.InstancedFullPath] = texture;
            TextureMap[textureExport.FullPath] = texture;
            TextureMap[textureExport.InstancedFullPath] = texture;
            return texture;
        }

        private void UpdateExpressions(UniformExpressionRenderContext uniformContext, 
            MaterialUniformExpression[] vectorExpressions, MaterialUniformExpression[] scalarExpressions, 
            List<Vector4> scalarCache, List<Vector4> vectorCache)
        {
            var enumerator = scalarExpressions.ChunkBySpan(4);
            foreach (ReadOnlySpan<MaterialUniformExpression> scalerExpression in enumerator)
            {
                LinearColor xVal = default;
                LinearColor yVal = default;
                LinearColor zVal = default;
                LinearColor wVal = default;
                scalerExpression[0].GetNumberValue(uniformContext, ref xVal);
                scalerExpression[1].GetNumberValue(uniformContext, ref yVal);
                scalerExpression[2].GetNumberValue(uniformContext, ref zVal);
                scalerExpression[3].GetNumberValue(uniformContext, ref wVal);
                scalarCache.Add(new Vector4(xVal.R, yVal.R, zVal.R, wVal.R));
            }
            if (enumerator.Current is { Length: > 0 } remainder)
            {
                LinearColor xVal = default;
                LinearColor yVal = default;
                LinearColor zVal = default;
                LinearColor wVal = default;
                remainder[0].GetNumberValue(uniformContext, ref xVal);
                if(remainder.Length > 1)
                {
                    remainder[1].GetNumberValue(uniformContext, ref yVal);
                    if (remainder.Length > 2)
                    {
                        remainder[2].GetNumberValue(uniformContext, ref zVal);
                        if (remainder.Length > 3)
                        {
                            remainder[3].GetNumberValue(uniformContext, ref wVal);
                        }
                    }
                }
                scalarCache.Add(new Vector4(xVal.R, yVal.R, zVal.R, wVal.R));
            }
            foreach (MaterialUniformExpression vectorExpression in vectorExpressions)
            {
                LinearColor val = default;
                vectorExpression.GetNumberValue(uniformContext, ref val);
                vectorCache.Add((Vector4)val);
            }
        }
    }
}
