using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using D2D = SharpDX.Direct2D1;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

// Fork extension points on upstream's renderer. The fork's VFX preview, galaxy map, BioStage and curve editors were written against the
// fork's renderer; these keep their hooks while every draw goes through this (upstream) renderer's caches, bind tracker and passes.
public partial class MeshRenderContext
{
    #region Shaders and samplers

    /// <summary>
    /// Scene depth for game materials that read SceneDepth (depth-biased alpha, soft particles) when no effects depth copy has been made this frame
    /// (see <see cref="EffectsSceneDepthView"/>, which the VFX and lens flare renderers make). Null, which samples as 0 (the far plane), unless a view
    /// supplies one
    /// </summary>
    public virtual ShaderResourceView PreviewSceneDepthTextureView => null;

    /// <summary>
    /// Fork API: a vertex shader with <typeparamref name="TVertex"/>'s input layout. Goes through upstream's cache, where a shader keeps the layout it was
    /// first cached with; <see cref="LEVertex"/> gets upstream's default layout (its streams plus <see cref="MeshStaticLighting"/>'s)
    /// </summary>
    public (VertexShader, InputLayout) GetCachedVertexShader<TVertex>(Guid id, byte[] shaderBytecode) where TVertex : IVertexBase
        => GetCachedVertexShader(id, shaderBytecode, typeof(TVertex) == typeof(LEVertex) ? null : TVertex.InputElements);

    /// <summary>
    /// Fork API: puts <see cref="SampleState"/> back in every pixel shader sampler slot. Upstream binds samplers only through
    /// <see cref="LEEffect.PixelShaderResources"/>, so this is <see cref="RestoreDefaultSamplers"/>
    /// </summary>
    public void ResetTextureSamplers() => RestoreDefaultSamplers();

    /// <summary>The sampler a texture is sampled with by the game: <see cref="SampleState"/> with the texture's address modes</summary>
    public SamplerState GetTextureSampler(PreviewTextureCache.TextureEntry texture)
        => texture is null ? SampleState : GetSamplerState(texture.AddressU, texture.AddressV);

    private Texture2D blackTex;
    private ShaderResourceView blackTexView;

    /// <summary>A 1x1 black texture (all channels 0), created on first use</summary>
    public ShaderResourceView BlackTexView
    {
        get
        {
            if (blackTexView is null && Device is not null)
            {
                blackTex = new Texture2D(Device, new Texture2DDescription { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm, SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.ShaderResource });
                int black = 0;
                ImmediateContext.UpdateSubresource(ref black, blackTex, rowPitch: 4);
                blackTexView = new ShaderResourceView(Device, blackTex);
            }
            return blackTexView;
        }
    }

    private void DisposeForkCompatResources()
    {
        blackTexView?.Dispose();
        blackTexView = null;
        blackTex?.Dispose();
        blackTex = null;
    }

    /// <summary>
    /// Verifies the reflected vertex-shader inputs are all supplied by <typeparamref name="TVertex"/>'s layout, with enough components.
    /// </summary>
    public static bool ValidateVertexShaderInputLayout<TVertex>(byte[] shaderBytecode, out string error) where TVertex : IVertexBase
    {
        error = null;
        if (shaderBytecode is null || shaderBytecode.Length == 0)
        {
            error = "The vertex shader has no bytecode.";
            return false;
        }

        using var reflection = new ShaderReflection(shaderBytecode);
        ShaderDescription shaderDescription = reflection.Description;
        for (int inputIndex = 0; inputIndex < shaderDescription.InputParameters; inputIndex++)
        {
            ShaderParameterDescription input = reflection.GetInputParameterDescription(inputIndex);
            if (input.SystemValueType is not SystemValueType.Undefined)
            {
                continue; //SV_VertexID and the like aren't vertex streams
            }
            InputElement? matchingElement = FindInputElement<TVertex>(input.SemanticName, input.SemanticIndex);
            if (matchingElement is null)
            {
                error = $"The {typeof(TVertex).Name} layout does not provide {input.SemanticName}{input.SemanticIndex}.";
                return false;
            }

            int requiredComponents = HighestSetBit((int)input.UsageMask);
            int suppliedComponents = GetFormatComponentCount(matchingElement.Value.Format);
            if (suppliedComponents < requiredComponents)
            {
                error = $"{input.SemanticName}{input.SemanticIndex} requires {requiredComponents} components, but {typeof(TVertex).Name} provides {suppliedComponents}.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Validates both sides of a native vertex-factory contract: the factory's canonical inputs, then the compiled shader's signature
    /// </summary>
    public static bool ValidateVertexFactoryInputLayout<TVertex>(string vertexFactoryType, byte[] shaderBytecode, out string error) where TVertex : IVertexBase
    {
        (string Semantic, int Index, int Components)[] requiredInputs = vertexFactoryType switch
        {
            "FLocalVertexFactory" =>
            [
                ("POSITION", 0, 4), ("TANGENT", 0, 3), ("NORMAL", 0, 4), ("COLOR", 1, 4),
                ("TEXCOORD", 0, 4), ("TEXCOORD", 1, 4), ("TEXCOORD", 2, 4), ("TEXCOORD", 3, 4)
            ],
            "FParticleVertexFactory" => ParticleBaseInputs(),
            "FLensFlareVertexFactory" =>
            [
                ("POSITION", 0, 4), ("TANGENT", 0, 4), ("BLENDWEIGHT", 0, 1),
                ("TEXCOORD", 0, 2), ("TEXCOORD", 1, 4), ("TEXCOORD", 2, 4)
            ],
            "FParticleSubUVVertexFactory" => [.. ParticleBaseInputs(), ("TEXCOORD", 2, 4)],
            "FParticleDynamicParameterVertexFactory" => [.. ParticleBaseInputs(), ("TEXCOORD", 3, 4)],
            "FParticleSubUVDynamicParameterVertexFactory" => [.. ParticleBaseInputs(), ("TEXCOORD", 2, 4), ("TEXCOORD", 3, 4)],
            "FParticleBeamTrailVertexFactory" => BeamTrailBaseInputs(),
            "FParticleBeamTrailDynamicParameterVertexFactory" => [.. BeamTrailBaseInputs(), ("TEXCOORD", 2, 4)],
            _ => null
        };
        if (requiredInputs is null)
        {
            error = $"No complete input contract is registered for {vertexFactoryType}.";
            return false;
        }

        foreach ((string semantic, int semanticIndex, int requiredComponents) in requiredInputs)
        {
            InputElement? matchingElement = FindInputElement<TVertex>(semantic, semanticIndex);
            if (matchingElement is null)
            {
                error = $"The {typeof(TVertex).Name} layout does not provide the {vertexFactoryType} input {semantic}{semanticIndex}.";
                return false;
            }
            int suppliedComponents = GetFormatComponentCount(matchingElement.Value.Format);
            if (suppliedComponents < requiredComponents)
            {
                error = $"The {vertexFactoryType} input {semantic}{semanticIndex} requires {requiredComponents} components, but {typeof(TVertex).Name} provides {suppliedComponents}.";
                return false;
            }
        }

        return ValidateVertexShaderInputLayout<TVertex>(shaderBytecode, out error);
    }

    private static (string Semantic, int Index, int Components)[] ParticleBaseInputs() =>
    [
        ("POSITION", 0, 4), ("NORMAL", 0, 4), ("TANGENT", 0, 3),
        ("BLENDWEIGHT", 0, 1), ("TEXCOORD", 0, 4), ("TEXCOORD", 1, 4)
    ];

    private static (string Semantic, int Index, int Components)[] BeamTrailBaseInputs() =>
    [
        ("POSITION", 0, 4), ("NORMAL", 0, 4), ("TANGENT", 0, 3),
        ("TEXCOORD", 0, 4), ("BLENDWEIGHT", 0, 1), ("TEXCOORD", 1, 4)
    ];

    private static InputElement? FindInputElement<TVertex>(string semantic, int semanticIndex) where TVertex : IVertexBase
    {
        foreach (InputElement element in TVertex.InputElements)
        {
            if (element.SemanticIndex == semanticIndex && string.Equals(element.SemanticName, semantic, StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }
        }
        return null;
    }

    private static int HighestSetBit(int mask)
    {
        int bit = 0;
        while (mask != 0)
        {
            bit++;
            mask >>= 1;
        }
        return bit;
    }

    private static int GetFormatComponentCount(Format format) => format switch
    {
        Format.R32_Float or Format.R32_UInt or Format.R32_SInt => 1,
        Format.R32G32_Float or Format.R32G32_UInt or Format.R32G32_SInt or Format.R16G16_Float or Format.R16G16_SNorm or Format.R16G16_UNorm => 2,
        Format.R32G32B32_Float or Format.R32G32B32_UInt or Format.R32G32B32_SInt => 3,
        Format.R32G32B32A32_Float or Format.R32G32B32A32_UInt or Format.R32G32B32A32_SInt
            or Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UInt or Format.R8G8B8A8_SNorm or Format.B8G8R8A8_UNorm
            or Format.R16G16B16A16_Float or Format.R16G16B16A16_SNorm or Format.R16G16B16A16_UNorm => 4,
        _ => 0
    };

    #endregion

    #region Game shader view space

    /// <summary>
    /// Fork: drew native shaders camera-relative while set. Upstream's game-shader path always works in translated world space (FSceneView's
    /// PreViewTranslation, see <see cref="LEEffect.PreViewTranslation"/>), which is camera-relative for perspective views, so this changes nothing
    /// </summary>
    internal bool UseCameraRelativeNativeRendering { get; set; }

    private Vector3 NativePreViewTranslation => Camera.IsOrthographic ? Vector3.Zero : -Camera.EyePosition;

    /// <summary>The view matrix of the translated world space game shaders work in (see <see cref="LEEffect.TranslatedViewProjectionMatrix"/>)</summary>
    internal Matrix4x4 GetNativeShaderViewMatrix() => Matrix4x4.CreateTranslation(-NativePreViewTranslation) * Camera.ViewMatrix;

    /// <summary>A local-to-world transform moved into translated world space</summary>
    internal Matrix4x4 GetNativeShaderLocalToWorld(Matrix4x4 localToWorld) => localToWorld * Matrix4x4.CreateTranslation(NativePreViewTranslation);

    /// <summary>The camera's position in translated world space (the origin, for a perspective view)</summary>
    internal Vector3 GetNativeShaderCameraPosition() => Camera.EyePosition + NativePreViewTranslation;

    internal Vector3 GetNativeShaderWorldPosition(Vector3 worldPosition) => worldPosition + NativePreViewTranslation;

    /// <summary>
    /// FMaterialPixelShaderParameters' ScreenPositionScaleBias: from clip space to render-target texture coordinates. Letterboxing
    /// (<see cref="ConstrainedAspectRatio"/>) is done in the projection, so this is the whole target's
    /// </summary>
    public Vector4 ScreenPositionScaleBias => LEEffect?.SharedPixelConstants.ScreenPositionScaleBias is { } bias && bias != Vector4.Zero
        ? bias : new Vector4(0.5f, -0.5f, 0.5f, 0.5f);

    /// <summary>Multiplies the neutral preview lighting (<see cref="Lighting"/>) game shaders are lit by. 1 is upstream's</summary>
    public float GameShaderLightScale { get; set; } = 1f;

    /// <summary>InvGamma given to game material shaders. 1 is upstream's</summary>
    public float GameShaderInvGamma { get; set; } = 1f;

    /// <summary>The channel mask (see <see cref="SceneLight.LightingChannelMask"/>) of the primitive being drawn. Kept for fork callers; upstream reads channels per light interaction</summary>
    public uint CurrentLightingChannelMask;

    #endregion

    #region Stats overlay

    /// <summary>Draws the frame rate (and camera position) in the corner. Upstream shows it in debug builds only, which stays the default</summary>
    public bool ShowStatsOverlay { get; set; } = App.IsDebug;

    public bool ShowCameraPositionInStatsOverlay { get; set; } = true;



    #endregion

    #region Letterboxing

    private float? constrainedAspectRatio;

    /// <summary>
    /// Optional aspect ratio for the 3D scene. When set, the scene is centered in the viewport at that ratio, with the rest left as background,
    /// rather than the camera's framing changing as the control is resized. Done in the projection (<see cref="SceneCamera.ViewportNdcScale"/>), so
    /// every pass, picking ray and projected point follows it
    /// </summary>
    public float? ConstrainedAspectRatio
    {
        get => constrainedAspectRatio;
        set
        {
            constrainedAspectRatio = value;
            ApplyViewportConstraint();
        }
    }

    public float SceneViewportX { get; private set; }
    public float SceneViewportY { get; private set; }
    public float SceneViewportWidth { get; private set; }
    public float SceneViewportHeight { get; private set; }

    public static (float X, float Y, float Width, float Height) CalculateFittedViewport(float availableWidth, float availableHeight, float? constrainedAspectRatio)
    {
        availableWidth = MathF.Max(availableWidth, 1f);
        availableHeight = MathF.Max(availableHeight, 1f);
        if (constrainedAspectRatio is not > 0f || !float.IsFinite(constrainedAspectRatio.Value))
        {
            return (0f, 0f, availableWidth, availableHeight);
        }

        float targetAspectRatio = constrainedAspectRatio.Value;
        float availableAspectRatio = availableWidth / availableHeight;
        if (availableAspectRatio > targetAspectRatio)
        {
            float viewportWidth = availableHeight * targetAspectRatio;
            return ((availableWidth - viewportWidth) * 0.5f, 0f, viewportWidth, availableHeight);
        }

        float viewportHeight = availableWidth / targetAspectRatio;
        return (0f, (availableHeight - viewportHeight) * 0.5f, availableWidth, viewportHeight);
    }

    /// <summary>Sets the camera's aspect and letterbox scale for the current size. Called when the size or <see cref="ConstrainedAspectRatio"/> changes</summary>
    private void ApplyViewportConstraint()
    {
        if (Width <= 0 || Height <= 0) return;
        (SceneViewportX, SceneViewportY, SceneViewportWidth, SceneViewportHeight) = CalculateFittedViewport(Width, Height, constrainedAspectRatio);
        Camera.aspect = SceneViewportWidth / SceneViewportHeight;
        Camera.ViewportNdcScale = new Vector2(SceneViewportWidth / Width, SceneViewportHeight / Height);
    }

    /// <summary>Whether a pixel is inside the letterboxed scene (always, without <see cref="ConstrainedAspectRatio"/>)</summary>
    public bool IsInSceneViewport(int x, int y) => constrainedAspectRatio is null
        || (x >= SceneViewportX && x < SceneViewportX + SceneViewportWidth && y >= SceneViewportY && y < SceneViewportY + SceneViewportHeight);

    /// <summary>A pixel's position in the scene viewport, from -1 to 1 (Y up)</summary>
    public Vector2 PixelToViewportNormalized(float x, float y)
    {
        float viewportX = constrainedAspectRatio is null ? 0 : SceneViewportX;
        float viewportY = constrainedAspectRatio is null ? 0 : SceneViewportY;
        float viewportWidth = MathF.Max(constrainedAspectRatio is null ? Width : SceneViewportWidth, 1f);
        float viewportHeight = MathF.Max(constrainedAspectRatio is null ? Height : SceneViewportHeight, 1f);
        return new Vector2((x - viewportX) / viewportWidth * 2f - 1f, 1f - (y - viewportY) / viewportHeight * 2f);
    }

    /// <summary>Covers what's outside the letterboxed scene with the background, on the resolved image (inside the D2D overlay's BeginDraw)</summary>
    private void DrawLetterboxBars()
    {
        if (constrainedAspectRatio is null || RenderTarget2D is null) return;
        float right = SceneViewportX + SceneViewportWidth, bottom = SceneViewportY + SceneViewportHeight;
        if (SceneViewportX < 0.5f && SceneViewportY < 0.5f && right > Width - 0.5f && bottom > Height - 0.5f) return;
        using var brush = new D2D.SolidColorBrush(RenderTarget2D, new RawColor4(BackgroundColor.R / 255f, BackgroundColor.G / 255f, BackgroundColor.B / 255f, 1));
        if (SceneViewportX > 0) RenderTarget2D.FillRectangle(new RawRectangleF(0, 0, SceneViewportX, Height), brush);
        if (right < Width) RenderTarget2D.FillRectangle(new RawRectangleF(right, 0, Width, Height), brush);
        if (SceneViewportY > 0) RenderTarget2D.FillRectangle(new RawRectangleF(0, 0, Width, SceneViewportY), brush);
        if (bottom < Height) RenderTarget2D.FillRectangle(new RawRectangleF(0, bottom, Width, Height), brush);
    }

    #endregion

    #region Package object caches

    /// <summary>An entry's export: itself, or its import resolved through <see cref="PackageCache"/> (which resolves each import once per load)</summary>
    internal ExportEntry ResolveExportCached(IMEPackage sourcePackage, int uIndex)
    {
        if (sourcePackage is null || uIndex == 0) return null;
        return ResolveExportCached(sourcePackage.GetEntry(uIndex));
    }

    internal ExportEntry ResolveExportCached(IEntry entry) => entry switch
    {
        ExportEntry export => export,
        ImportEntry import => PackageCache.ResolveImport(import),
        _ => null
    };

    /// <summary>Fork API: a static mesh's parsed binary, from upstream's mesh cache (<see cref="GetStaticMesh"/>)</summary>
    internal StaticMesh GetCachedStaticMesh(ExportEntry export) => GetStaticMesh(export);

    internal SkeletalMesh GetCachedSkeletalMesh(ExportEntry export) => export.GetBinaryData<SkeletalMesh>();

    private readonly Dictionary<(IMEPackage Package, int UIndex), AnimSequence> forkAnimSequences = [];
    private readonly Dictionary<(IMEPackage Package, int AnimSet, string Name), AnimSequence> forkConfiguredAnimations = [];

    /// <summary>
    /// Resolves an actor's configured animation by name through its AnimSet/BioDynamicAnimSet references. Lookups (failed ones too) are cached
    /// until the package changes, since character-heavy levels reuse the same sets and poses many times
    /// </summary>
    internal AnimSequence ResolveConfiguredAnimation(IMEPackage sourcePackage, IReadOnlyList<int> animSetUIndexes, NameReference animationName)
    {
        if (sourcePackage is null || animSetUIndexes is null || animSetUIndexes.Count == 0
            || string.IsNullOrWhiteSpace(animationName.Name)
            || animationName.Name.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string lookupName = animationName.Instanced.ToUpperInvariant();
        lock (forkConfiguredAnimations)
        {
            foreach (int animSetUIndex in animSetUIndexes)
            {
                if (animSetUIndex == 0) continue;
                var lookupKey = (sourcePackage, animSetUIndex, lookupName);
                if (forkConfiguredAnimations.TryGetValue(lookupKey, out AnimSequence cachedAnimation))
                {
                    if (cachedAnimation is not null) return cachedAnimation;
                    continue;
                }

                AnimSequence resolvedAnimation = null;
                ExportEntry animSet = ResolveExportCached(sourcePackage, animSetUIndex);
                if (animSet?.GetProperty<ArrayProperty<ObjectProperty>>("Sequences") is { } sequences)
                {
                    foreach (ObjectProperty sequenceReference in sequences)
                    {
                        ExportEntry sequenceExport = ResolveExportCached(animSet.FileRef, sequenceReference.Value);
                        if (sequenceExport?.ClassName != "AnimSequence") continue;
                        NameReference sequenceName = sequenceExport.GetProperty<NameProperty>("SequenceName")?.Value ?? sequenceExport.ObjectName;
                        if (sequenceName.Name.Equals(animationName.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            resolvedAnimation = GetCachedAnimSequence(sequenceExport);
                            break;
                        }
                    }
                }

                forkConfiguredAnimations[lookupKey] = resolvedAnimation;
                if (resolvedAnimation is not null) return resolvedAnimation;
            }
        }
        return null;
    }

    private AnimSequence GetCachedAnimSequence(ExportEntry export)
    {
        var key = (export.FileRef, export.UIndex);
        if (!forkAnimSequences.TryGetValue(key, out AnimSequence animation))
        {
            animation = ObjectBinary.From<AnimSequence>(export, PackageCache);
            animation.DecompressAnimationData();
            forkAnimSequences.Add(key, animation);
        }
        return animation;
    }

    /// <summary>Forgets the fork caches above: all of them, or those of one package</summary>
    private void ClearForkCompatCaches(IMEPackage package = null)
    {
        lock (forkConfiguredAnimations)
        {
            if (package is null)
            {
                forkConfiguredAnimations.Clear();
                forkAnimSequences.Clear();
                return;
            }
            //an animation set can resolve into other packages, so any change forgets every lookup; only the edited package's sequences are dropped
            forkConfiguredAnimations.Clear();
            foreach (var key in new List<(IMEPackage, int)>(forkAnimSequences.Keys))
            {
                if (key.Item1 == package) forkAnimSequences.Remove(key);
            }
        }
    }

    #endregion

    #region Static scene cache (fork)

    /// <summary>
    /// Fork: the curve editor cached a static backdrop's color and depth between frames. Upstream draws every frame, and keeps per-frame work low
    /// itself, so there's no cache: restoring always fails, and the caller renders the scene as it would without one
    /// </summary>
    public bool TryRestoreStaticSceneCache(long sceneKey, bool restoreHitBuffer = false) => false;

    public void BeginStaticSceneCacheCandidate() { }

    public bool CaptureStaticSceneCache(long sceneKey, bool captureHitBuffer = false) => false;

    public void InvalidateStaticSceneCache() { }

    #endregion

    #region Picking and culling

    /// <summary>
    /// Reads the average color of the 3x3 backbuffer region centered on a pixel (the last presented frame, gamma-encoded). For the live
    /// material editor's color picking
    /// </summary>
    public unsafe bool TryReadBackbufferPixelNeighborhood(int x, int y, out Vector4 color)
    {
        color = default;
        if (Backbuffer is null || Width <= 0 || Height <= 0) return false;

        int minX = Math.Clamp(x - 1, 0, Width - 1);
        int maxX = Math.Clamp(x + 1, 0, Width - 1);
        int minY = Math.Clamp(y - 1, 0, Height - 1);
        int maxY = Math.Clamp(y + 1, 0, Height - 1);
        int sampleWidth = maxX - minX + 1;
        int sampleHeight = maxY - minY + 1;

        Format format = Backbuffer.Description.Format;
        bool isBgra = format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb;
        bool isRgba = format is Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb;
        if (!isBgra && !isRgba) return false;

        using var readback = new Texture2D(Device, new Texture2DDescription
        {
            ArraySize = 1,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            Format = format,
            Height = sampleHeight,
            Width = sampleWidth,
            MipLevels = 1,
            OptionFlags = ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging
        });
        ImmediateContext.CopySubresourceRegion(Backbuffer, 0, new ResourceRegion(minX, minY, 0, maxX + 1, maxY + 1, 1), readback, 0);
        SharpDX.DataBox mapped = ImmediateContext.MapSubresource(readback, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            Vector4 sum = default;
            for (int sampleY = 0; sampleY < sampleHeight; sampleY++)
            {
                var row = new Span<SharpDX.Color>((mapped.DataPointer + sampleY * mapped.RowPitch).ToPointer(), sampleWidth);
                foreach (SharpDX.Color pixel in row)
                {
                    sum.X += (isBgra ? pixel.B : pixel.R) / 255f;
                    sum.Y += pixel.G / 255f;
                    sum.Z += (isBgra ? pixel.R : pixel.B) / 255f;
                    sum.W += pixel.A / 255f;
                }
            }
            color = sum / (sampleWidth * sampleHeight);
            return true;
        }
        finally
        {
            ImmediateContext.UnmapSubresource(readback, 0);
        }
    }

    /// <summary>
    /// Fork: per-frame culling of bounds against the camera. Built on upstream's <see cref="ViewFrustum"/> (the camera's view-projection, without
    /// depth clipping, since perspective views have no far plane). Zero, invalid and extent-less bounds stay visible
    /// </summary>
    public readonly struct BoundsVisibilityTester
    {
        private readonly ViewFrustum frustum;

        internal BoundsVisibilityTester(SceneCamera camera)
        {
            frustum = new ViewFrustum();
            frustum.Update(camera.ViewProjectionMatrix, depthClipEnabled: false);
        }

        public bool IsVisible(BoxSphereBounds bounds)
        {
            if (frustum is null) return true;
            if (!float.IsFinite(bounds.Origin.X) || !float.IsFinite(bounds.Origin.Y) || !float.IsFinite(bounds.Origin.Z)
                || !float.IsFinite(bounds.SphereRadius) || !float.IsFinite(bounds.BoxExtent.X) || !float.IsFinite(bounds.BoxExtent.Y)
                || !float.IsFinite(bounds.BoxExtent.Z))
            {
                return true;
            }
            if (bounds.BoxExtent == Vector3.Zero)
            {
                if (!(bounds.SphereRadius > 0)) return true;
                float r = bounds.SphereRadius;
                bounds = new BoxSphereBounds { Origin = bounds.Origin, BoxExtent = new Vector3(r), SphereRadius = r };
            }
            return frustum.Intersects(bounds);
        }
    }

    public BoundsVisibilityTester CreateBoundsVisibilityTester() => new(Camera);

    public bool IsBoundsVisible(BoxSphereBounds bounds) => CreateBoundsVisibilityTester().IsVisible(bounds);

    #endregion

    #region Preview lights (fork SceneLights)

    private SceneLightCollection sceneLights;

    /// <summary>
    /// Fork: the scene's preview lights. Lights read from packages (with an export) are upstream level lights, so adding or removing them here
    /// adds them to or removes them from <see cref="GetLights"/>; free-standing preview lights (see the <see cref="SceneLight"/> fork constructor) are
    /// only kept here, since upstream lights previews with <see cref="Lighting"/>
    /// </summary>
    public SceneLightCollection SceneLights => sceneLights ??= new SceneLightCollection(this);

    /// <summary>Changes whenever <see cref="SceneLights"/> does</summary>
    public int SceneLightRevision => sceneLights?.Revision ?? 0;

    #endregion
}

/// <summary>
/// Fork's list of preview lights, mapped onto upstream's level light list (<see cref="MeshRenderContext.AddLights"/>): see <see cref="MeshRenderContext.SceneLights"/>
/// </summary>
public sealed class SceneLightCollection : IReadOnlyList<SceneLight>
{
    private readonly MeshRenderContext context;
    private readonly List<SceneLight> lights = [];
    internal int Revision { get; private set; }

    internal SceneLightCollection(MeshRenderContext context) => this.context = context;

    public int Count => lights.Count;
    public SceneLight this[int index] => lights[index];

    public void Add(SceneLight light)
    {
        if (light is null) return;
        lights.Add(light);
        if (light.Export is not null) context.AddLights([light]);
        Revision++;
    }

    public void AddRange(IEnumerable<SceneLight> collection)
    {
        foreach (SceneLight light in collection) Add(light);
    }

    public bool Remove(SceneLight light)
    {
        if (!lights.Remove(light)) return false;
        if (light.Export is not null) context.RemoveLights([light]);
        Revision++;
        return true;
    }

    public void RemoveAt(int index) => Remove(lights[index]);

    /// <summary>Removes the lights added here (level lights the scene loaded itself stay)</summary>
    public void Clear()
    {
        List<SceneLight> levelLights = lights.FindAll(light => light.Export is not null);
        if (levelLights.Count > 0) context.RemoveLights(levelLights);
        lights.Clear();
        Revision++;
    }

    public bool Contains(SceneLight light) => lights.Contains(light);

    public IEnumerator<SceneLight> GetEnumerator() => lights.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

}

public partial class MeshRenderContext
{
    private ViewportLightingMode lightingModeBeforeUnlit = ViewportLightingMode.Level;

    /// <summary>
    /// Fork tools' Unlit toggle (BioStage, curve editor, camera presets). Upstream unlights game-shaded meshes through
    /// <see cref="ViewportLightingMode.Unlit"/>, and LEX's fallback shader through <see cref="RenderContext.ShaderFlags.Unlit"/>; this sets both,
    /// and turning it off returns to the lighting mode in use before
    /// </summary>
    public void SetUnlit(bool unlit)
    {
        if (unlit)
        {
            if (LightingMode != ViewportLightingMode.Unlit) lightingModeBeforeUnlit = LightingMode;
            LightingMode = ViewportLightingMode.Unlit;
            RenderFlags |= ShaderFlags.Unlit;
        }
        else
        {
            if (LightingMode == ViewportLightingMode.Unlit) LightingMode = lightingModeBeforeUnlit;
            RenderFlags &= ~ShaderFlags.Unlit;
        }
    }
}
