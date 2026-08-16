using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Matrix = SharpDX.Matrix;

namespace LE1GalaxyMapEditor.Rendering;

public sealed record PlanetPreviewTextureSource(string CacheKey, byte[] Contents);

public sealed class PlanetPreviewRenderer : IDisposable
{
    private int _width;
    private int _height;
    private readonly Device _device;
    private readonly DeviceContext _context;
    private readonly string _resourceRoot;
    private readonly Func<string, PlanetPreviewTextureSource?>? _materialTextureResolver;
    private readonly Dictionary<(string FileName, bool Srgb), TextureResource> _textureCache = [];
    private readonly HashSet<string> _missingTextures = new(StringComparer.OrdinalIgnoreCase);

    private VertexShader _planetVertexShader = null!;
    private PixelShader _planetPixelShader = null!;
    private VertexShader _coronaVertexShader = null!;
    private PixelShader _coronaPixelShader = null!;
    private VertexShader _backgroundVertexShader = null!;
    private PixelShader _backgroundPixelShader = null!;
    private VertexShader _postProcessVertexShader = null!;
    private PixelShader _bloomExtractPixelShader = null!;
    private PixelShader _bloomBlurPixelShader = null!;
    private PixelShader _compositePixelShader = null!;
    private InputLayout _inputLayout = null!;
    private MeshBuffers _planetMesh = null!;
    private MeshBuffers _coronaMesh = null!;
    private Buffer _sceneBuffer = null!;
    private Buffer _materialBuffer = null!;
    private Buffer _coronaMaterialBuffer = null!;
    private Buffer _postProcessBuffer = null!;
    private Texture2D _sceneTexture = null!;
    private RenderTargetView _sceneRenderTarget = null!;
    private ShaderResourceView _sceneShaderResource = null!;
    private BloomLevel[] _bloomLevels = [];
    private Texture2D _renderTexture = null!;
    private RenderTargetView _renderTarget = null!;
    private Texture2D _depthTexture = null!;
    private DepthStencilView _depthView = null!;
    private Texture2D _stagingTexture = null!;
    private RasterizerState _rasterizer = null!;
    private SamplerState _sampler = null!;
    private SamplerState _postProcessSampler = null!;
    private BlendState _coronaBlendState = null!;
    private DepthStencilState _coronaDepthState = null!;
    private DepthStencilState _backgroundDepthState = null!;
    private TextureResource _coronaGradient = null!;
    private TextureResource _starsBackground = null!;

    public PlanetPreviewRenderer(
        int width,
        int height,
        string? resourceRoot = null,
        Func<string, PlanetPreviewTextureSource?>? materialTextureResolver = null)
    {
        _width = width;
        _height = height;
        _resourceRoot = resourceRoot ?? Path.Combine(AppContext.BaseDirectory, "resources", "planet-designer");
        _materialTextureResolver = materialTextureResolver;
        try
        {
            _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }
        catch (SharpDXException)
        {
            _device = new Device(DriverType.Warp, DeviceCreationFlags.BgraSupport);
        }
        _context = _device.ImmediateContext;
        InitializeGpuResources();
    }

    private void InitializeGpuResources()
    {
        var planetShaderPath = Path.Combine(_resourceRoot, "Shaders", "EarthPrototype.hlsl");
        using var planetVertexBytecode = CompileShader(planetShaderPath, "VSMain", "vs_5_0");
        using var planetPixelBytecode = CompileShader(planetShaderPath, "PSMain", "ps_5_0");
        _planetVertexShader = new VertexShader(_device, planetVertexBytecode);
        _planetPixelShader = new PixelShader(_device, planetPixelBytecode);

        var coronaShaderPath = Path.Combine(_resourceRoot, "Shaders", "CoronaPrototype.hlsl");
        using var coronaVertexBytecode = CompileShader(coronaShaderPath, "VSMain", "vs_5_0");
        using var coronaPixelBytecode = CompileShader(coronaShaderPath, "PSMain", "ps_5_0");
        _coronaVertexShader = new VertexShader(_device, coronaVertexBytecode);
        _coronaPixelShader = new PixelShader(_device, coronaPixelBytecode);

        var backgroundShaderPath = Path.Combine(_resourceRoot, "Shaders", "Background.hlsl");
        using var backgroundVertexBytecode = CompileShader(backgroundShaderPath, "VSMain", "vs_5_0");
        using var backgroundPixelBytecode = CompileShader(backgroundShaderPath, "PSMain", "ps_5_0");
        _backgroundVertexShader = new VertexShader(_device, backgroundVertexBytecode);
        _backgroundPixelShader = new PixelShader(_device, backgroundPixelBytecode);

        var postProcessShaderPath = Path.Combine(_resourceRoot, "Shaders", "PostProcess.hlsl");
        using var postProcessVertexBytecode = CompileShader(postProcessShaderPath, "VSMain", "vs_5_0");
        using var bloomExtractBytecode = CompileShader(postProcessShaderPath, "BloomExtractPS", "ps_5_0");
        using var bloomBlurBytecode = CompileShader(postProcessShaderPath, "BloomBlurPS", "ps_5_0");
        using var compositeBytecode = CompileShader(postProcessShaderPath, "CompositePS", "ps_5_0");
        _postProcessVertexShader = new VertexShader(_device, postProcessVertexBytecode);
        _bloomExtractPixelShader = new PixelShader(_device, bloomExtractBytecode);
        _bloomBlurPixelShader = new PixelShader(_device, bloomBlurBytecode);
        _compositePixelShader = new PixelShader(_device, compositeBytecode);

        var inputElements = new[]
        {
            new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElement("TANGENT", 0, Format.R32G32B32A32_Float, 24, 0),
            new InputElement("TEXCOORD", 0, Format.R32G32_Float, 40, 0)
        };
        _inputLayout = new InputLayout(
            _device,
            ShaderSignature.GetInputSignature(planetVertexBytecode),
            inputElements);

        _planetMesh = CreateMeshBuffers(GltfMesh.Load(
            Path.Combine(_resourceRoot, "Meshes", "GXM10_Planet01.glTF")));
        _coronaMesh = CreateMeshBuffers(GltfMesh.Load(
            Path.Combine(_resourceRoot, "Meshes", "GXM10_Corona01.glTF")));

        _sceneBuffer = CreateConstantBuffer<SceneConstants>();
        _materialBuffer = CreateConstantBuffer<MaterialConstants>();
        _coronaMaterialBuffer = CreateConstantBuffer<CoronaMaterialConstants>();
        _postProcessBuffer = CreateConstantBuffer<PostProcessConstants>();

        CreateRenderTargets();
        _rasterizer = new RasterizerState(_device, new RasterizerStateDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            IsDepthClipEnabled = true
        });
        _sampler = new SamplerState(_device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaximumLod = float.MaxValue
        });
        _postProcessSampler = new SamplerState(_device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaximumLod = float.MaxValue
        });

        var blendDescription = new BlendStateDescription();
        blendDescription.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true,
            SourceBlend = BlendOption.One,
            DestinationBlend = BlendOption.One,
            BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = BlendOption.One,
            DestinationAlphaBlend = BlendOption.One,
            AlphaBlendOperation = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteMaskFlags.All
        };
        _coronaBlendState = new BlendState(_device, blendDescription);
        _coronaDepthState = new DepthStencilState(_device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.LessEqual,
            IsStencilEnabled = false
        });
        _backgroundDepthState = new DepthStencilState(_device, new DepthStencilStateDescription
        {
            IsDepthEnabled = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.Always,
            IsStencilEnabled = false
        });
        _coronaGradient = LoadResolvedTexture(
            "BIOA_GXM10_T.GXM_CoronaGradient",
            "GXM_CoronaGradient.png",
            useSrgb: true);
        // Hardware sRGB decoding puts the background into the same linear scene
        // space as the mesh shaders before the full-screen postprocess.
        _starsBackground = LoadTexture("stars_bg.jpg", useSrgb: true);
    }

    private void CreateRenderTargets()
    {
        _sceneTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });
        _sceneRenderTarget = new RenderTargetView(_device, _sceneTexture);
        _sceneShaderResource = new ShaderResourceView(_device, _sceneTexture);

        // The measured UE3 halo is a single sigma ~= 6.86 px Gaussian at
        // 1080p. This quarter-resolution pass produces sigma ~= 6.55 px.
        _bloomLevels = [CreateBloomLevel(4, blurIterations: 1)];

        _renderTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });
        _renderTarget = new RenderTargetView(_device, _renderTexture);
        _depthTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil
        });
        _depthView = new DepthStencilView(_device, _depthTexture);
        _stagingTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read
        });
    }

    private BloomLevel CreateBloomLevel(int divisor, int blurIterations)
    {
        var width = Math.Max(1, (_width + divisor - 1) / divisor);
        var height = Math.Max(1, (_height + divisor - 1) / divisor);
        return new BloomLevel(
            width,
            height,
            blurIterations,
            CreatePostProcessTarget(width, height),
            CreatePostProcessTarget(width, height));
    }

    private PostProcessTarget CreatePostProcessTarget(int width, int height)
    {
        var texture = new Texture2D(_device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });
        return new PostProcessTarget(
            texture,
            new RenderTargetView(_device, texture),
            new ShaderResourceView(_device, texture));
    }

    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width == _width && height == _height)
        {
            return;
        }

        _context.ClearState();
        DisposeRenderTargets();
        _width = width;
        _height = height;
        CreateRenderTargets();
    }

    private void DisposeRenderTargets()
    {
        _stagingTexture.Dispose();
        _depthView.Dispose();
        _depthTexture.Dispose();
        _renderTarget.Dispose();
        _renderTexture.Dispose();
        foreach (var bloomLevel in _bloomLevels)
        {
            bloomLevel.Dispose();
        }
        _bloomLevels = [];
        _sceneShaderResource.Dispose();
        _sceneRenderTarget.Dispose();
        _sceneTexture.Dispose();
    }

    private Buffer CreateConstantBuffer<T>() where T : struct => new(
        _device,
        Utilities.SizeOf<T>(),
        ResourceUsage.Default,
        BindFlags.ConstantBuffer,
        CpuAccessFlags.None,
        ResourceOptionFlags.None,
        0);

    private MeshBuffers CreateMeshBuffers((SphereMesh.Vertex[] Vertices, uint[] Indices) mesh) => new(
        Buffer.Create(_device, BindFlags.VertexBuffer, mesh.Vertices),
        Buffer.Create(_device, BindFlags.IndexBuffer, mesh.Indices),
        mesh.Indices.Length);

    public PlanetPreviewFrame Render(
        PlanetRenderMaterial material,
        PlanetPreviewOptions options,
        float timeSeconds = 0)
    {
        return RenderCore(material, options, timeSeconds, destination: null);
    }

    /// <summary>
    /// Renders into a caller-owned buffer. The returned frame references the supplied
    /// buffer, which may be reused by the caller after the frame has been consumed.
    /// </summary>
    public PlanetPreviewFrame Render(
        PlanetRenderMaterial material,
        PlanetPreviewOptions options,
        byte[] destination,
        float timeSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var expectedLength = checked(_width * _height * 4);
        if (destination.Length != expectedLength)
        {
            throw new ArgumentException(
                $"The destination buffer must contain exactly {expectedLength} bytes for a {_width}x{_height} BGRA frame.",
                nameof(destination));
        }

        return RenderCore(material, options, timeSeconds, destination);
    }

    private PlanetPreviewFrame RenderCore(
        PlanetRenderMaterial material,
        PlanetPreviewOptions options,
        float timeSeconds,
        byte[]? destination)
    {
        ArgumentNullException.ThrowIfNull(material);
        _missingTextures.Clear();
        var stopwatch = Stopwatch.StartNew();
        var pixels = RenderEarthFrame(
            new ValidationMode(
                options.Lit,
                options.PointLights,
                options.PostProcessed,
                options.Corona,
                options.Stars),
            timeSeconds,
            material,
            new PreviewTransformSettings(),
            new PreviewLightingSettings(),
            destination);
        stopwatch.Stop();
        return new PlanetPreviewFrame(
            pixels,
            _width,
            _height,
            stopwatch.Elapsed,
            _missingTextures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private byte[] RenderEarthFrame(
        ValidationMode validationMode,
        float timeSeconds,
        PlanetRenderMaterial material,
        PreviewTransformSettings transforms,
        PreviewLightingSettings lighting,
        byte[]? destination)
    {
        var normal = LoadMaterialTexture(material.NormalMap, "BIOA_GXM10_T.GXM_PlanetNormal01", useSrgb: false);
        var city = LoadMaterialTexture(material.CityEmissive, "BIOA_GXM10_T.GXM_ContinentMask02", useSrgb: true);
        var continentMask1 = LoadMaterialTexture(material.ContinentMask01, "BIOA_GXM10_T.GXM_ContinentMask01", useSrgb: true);
        var continentMask2 = LoadMaterialTexture(material.ContinentMask02, "BIOA_GXM10_T.GXM_DiffuseMask01", useSrgb: true);
        var continentTexture = LoadMaterialTexture(material.ContinentTexture, "BIOA_GXM10_T.GXM_DiffuseMask01", useSrgb: true);
        var oceanTexture = LoadMaterialTexture(material.OceanTexture, "BIOA_GXM10_T.GXM_DiffuseMask01", useSrgb: true);
        var atmosphere = LoadMaterialTexture(material.AtmosphereMaster, "BIOA_GXM10_T.GXM_Atmosphere01", useSrgb: true);
        var textures = new[]
        {
            normal.View,
            city.View,
            continentMask1.View,
            continentMask2.View,
            continentTexture.View,
            oceanTexture.View,
            atmosphere.View
        };

        // Rebase the supplied UE3 actor positions around the original planet
        // origin. This keeps the debug fields in familiar UE units.
        var cameraLocationUe = new Vector3(-5040, 13284, -39833);
        var previewOriginUe = new Vector3(-5406.77295f, 13571.8086f, -40187.1992f);
        var planetLocationUe = new Vector3(
            transforms.Planet.X,
            transforms.Planet.Y,
            transforms.Planet.Z);
        var cameraPosition = ConvertUnrealPositionToGltf(cameraLocationUe - previewOriginUe);

        var cameraRotationUe = CreateUnrealRotation(27, -192, 0);
        var forward = ConvertUnrealDirectionToGltf(new Vector3(
            cameraRotationUe.M11,
            cameraRotationUe.M12,
            cameraRotationUe.M13));
        var cameraUp = ConvertUnrealDirectionToGltf(new Vector3(
            cameraRotationUe.M31,
            cameraRotationUe.M32,
            cameraRotationUe.M33));

        var world = Matrix.Scaling(transforms.Planet.Scale) * ConvertUnrealRotationToGltf(
                CreateUnrealRotation(
                    transforms.Planet.Pitch,
                    transforms.Planet.Yaw,
                    transforms.Planet.Roll)) *
            Matrix.Translation(ConvertUnrealPositionToGltf(planetLocationUe - previewOriginUe));
        var view = Matrix.LookAtRH(cameraPosition, cameraPosition + forward, cameraUp);

        // UE3's common 90-degree camera FOV is horizontal. Convert it to
        // the vertical FOV expected by the D3D projection helper.
        var aspectRatio = _width / (float)_height;
        var verticalFov = 2 * MathF.Atan(MathF.Tan(MathUtil.DegreesToRadians(90) / 2) / aspectRatio);
        var projection = Matrix.PerspectiveFovRH(
            verticalFov,
            aspectRatio,
            0.01f,
            100f);
        var scene = new SceneConstants
        {
            World = world,
            WorldViewProjection = world * view * projection,
            CameraPosition = new Vector4(cameraPosition, 1),
            RenderOptions = new Vector4(
                validationMode.Lit ? 1 : 0,
                validationMode.PostProcessed ? 1 : 0,
                timeSeconds,
                validationMode.PointLights ? 1 : 0)
        };
        var previewOriginUeForLights = new Vector3(-5406.77295f, 13571.8086f, -40187.1992f);
        var light1Position = ConvertUnrealPositionToGltf(new Vector3(
            lighting.Light1.X,
            lighting.Light1.Y,
            lighting.Light1.Z) - previewOriginUeForLights);
        var light2Position = ConvertUnrealPositionToGltf(new Vector3(
            lighting.Light2.X,
            lighting.Light2.Y,
            lighting.Light2.Z) - previewOriginUeForLights);
        var materialConstants = MaterialConstants.From(
            material,
            lighting,
            light1Position,
            light2Position);

        _context.OutputMerger.SetTargets(_depthView, _sceneRenderTarget);
        _context.Rasterizer.SetViewport(0, 0, _width, _height);
        _context.Rasterizer.State = _rasterizer;
        _context.ClearRenderTargetView(_sceneRenderTarget, new Color4(0.003f, 0.005f, 0.012f, 1));
        _context.ClearDepthStencilView(_depthView, DepthStencilClearFlags.Depth, 1, 0);
        if (validationMode.Stars)
        {
            DrawBackground();
        }
        _context.InputAssembler.InputLayout = _inputLayout;
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        var mesh = _planetMesh;
        _context.InputAssembler.SetVertexBuffers(
            0,
            new VertexBufferBinding(mesh.VertexBuffer, Marshal.SizeOf<SphereMesh.Vertex>(), 0));
        _context.InputAssembler.SetIndexBuffer(mesh.IndexBuffer, Format.R32_UInt, 0);
        _context.VertexShader.Set(_planetVertexShader);
        _context.VertexShader.SetConstantBuffer(0, _sceneBuffer);
        _context.PixelShader.Set(_planetPixelShader);
        _context.PixelShader.SetConstantBuffer(0, _sceneBuffer);
        _context.PixelShader.SetConstantBuffer(1, _materialBuffer);
        _context.PixelShader.SetSampler(0, _sampler);
        _context.PixelShader.SetShaderResources(0, textures);
        _context.UpdateSubresource(ref scene, _sceneBuffer);
        _context.UpdateSubresource(ref materialConstants, _materialBuffer);
        _context.DrawIndexed(mesh.IndexCount, 0, 0);

        _context.PixelShader.SetShaderResources(0, new ShaderResourceView?[textures.Length]);

        if (validationMode.Corona)
        {
            DrawCorona(
                view,
                projection,
                cameraPosition,
                material,
                transforms);
        }

        ApplyPostProcess(validationMode.PostProcessed);
        return ReadTexture(destination);
    }

    private void DrawBackground()
    {
        _context.InputAssembler.InputLayout = null;
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(null, 0, 0));
        _context.InputAssembler.SetIndexBuffer(null, Format.Unknown, 0);
        _context.VertexShader.Set(_backgroundVertexShader);
        _context.PixelShader.Set(_backgroundPixelShader);
        _context.PixelShader.SetSampler(0, _sampler);
        _context.PixelShader.SetShaderResource(0, _starsBackground.View);
        _context.OutputMerger.SetBlendState(null);
        _context.OutputMerger.SetDepthStencilState(_backgroundDepthState);
        _context.Draw(3, 0);
        _context.PixelShader.SetShaderResource(0, null);
        _context.OutputMerger.SetDepthStencilState(null);
    }

    private void DrawCorona(
        Matrix view,
        Matrix projection,
        Vector3 cameraPosition,
        PlanetRenderMaterial material,
        PreviewTransformSettings transforms)
    {
        _coronaGradient = LoadResolvedTexture(
            "BIOA_GXM10_T.GXM_CoronaGradient",
            "GXM_CoronaGradient.png",
            useSrgb: true);
        var previewOriginUe = new Vector3(-5406.77295f, 13571.8086f, -40187.1992f);
        var coronaLocationUe = new Vector3(
            transforms.Corona.X,
            transforms.Corona.Y,
            transforms.Corona.Z);
        var world =
            Matrix.Scaling(transforms.Corona.Scale) *
            ConvertUnrealRotationToGltf(CreateUnrealRotation(
                transforms.Corona.Pitch,
                transforms.Corona.Yaw,
                transforms.Corona.Roll)) *
            Matrix.Translation(ConvertUnrealPositionToGltf(coronaLocationUe - previewOriginUe));
        var scene = new SceneConstants
        {
            World = world,
            WorldViewProjection = world * view * projection,
            CameraPosition = new Vector4(cameraPosition, 1),
            RenderOptions = new Vector4(0, 0, 0, 0)
        };
        var coronaMaterial = new CoronaMaterialConstants
        {
            CoronaColor = ToDx(material.CoronaColor),
            CoronaScalars = new Vector4(material.FringeBloom, material.Opacity, 0, 0)
        };

        _context.InputAssembler.InputLayout = _inputLayout;
        _context.InputAssembler.SetVertexBuffers(
            0,
            new VertexBufferBinding(_coronaMesh.VertexBuffer, Marshal.SizeOf<SphereMesh.Vertex>(), 0));
        _context.InputAssembler.SetIndexBuffer(_coronaMesh.IndexBuffer, Format.R32_UInt, 0);
        _context.VertexShader.Set(_coronaVertexShader);
        _context.VertexShader.SetConstantBuffer(0, _sceneBuffer);
        _context.PixelShader.Set(_coronaPixelShader);
        _context.PixelShader.SetConstantBuffer(0, _sceneBuffer);
        _context.PixelShader.SetConstantBuffer(1, _coronaMaterialBuffer);
        _context.PixelShader.SetSampler(0, _sampler);
        _context.PixelShader.SetShaderResource(0, _coronaGradient.View);
        _context.OutputMerger.SetBlendState(_coronaBlendState);
        _context.OutputMerger.SetDepthStencilState(_coronaDepthState);
        _context.UpdateSubresource(ref scene, _sceneBuffer);
        _context.UpdateSubresource(ref coronaMaterial, _coronaMaterialBuffer);
        _context.DrawIndexed(_coronaMesh.IndexCount, 0, 0);

        _context.PixelShader.SetShaderResource(0, null);
        _context.OutputMerger.SetBlendState(null);
        _context.OutputMerger.SetDepthStencilState(null);
    }

    private void ApplyPostProcess(bool enabled)
    {
        var constants = new PostProcessConstants
        {
            BloomParameters = new Vector4(
                1.5f,
                0.34f,
                0.5f,
                enabled ? 1 : 0),
            PassParameters = Vector4.Zero
        };

        _context.InputAssembler.InputLayout = null;
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(null, 0, 0));
        _context.InputAssembler.SetIndexBuffer(null, Format.Unknown, 0);
        _context.VertexShader.Set(_postProcessVertexShader);
        _context.PixelShader.SetConstantBuffer(0, _postProcessBuffer);
        _context.PixelShader.SetSampler(0, _postProcessSampler);
        _context.OutputMerger.SetBlendState(null);
        _context.OutputMerger.SetDepthStencilState(_backgroundDepthState);

        if (enabled)
        {
            foreach (var bloomLevel in _bloomLevels)
            {
                constants.PassParameters = new Vector4(0, 0, 1f / bloomLevel.Width, 1f / bloomLevel.Height);
                _context.Rasterizer.SetViewport(0, 0, bloomLevel.Width, bloomLevel.Height);
                _context.OutputMerger.SetTargets((DepthStencilView?)null, bloomLevel.A.RenderTarget);
                _context.PixelShader.Set(_bloomExtractPixelShader);
                _context.PixelShader.SetShaderResource(0, _sceneShaderResource);
                _context.UpdateSubresource(ref constants, _postProcessBuffer);
                _context.Draw(3, 0);
                _context.PixelShader.SetShaderResource(0, null);

                for (var iteration = 0; iteration < bloomLevel.BlurIterations; iteration++)
                {
                    constants.PassParameters.X = 1;
                    constants.PassParameters.Y = 0;
                    _context.OutputMerger.SetTargets((DepthStencilView?)null, bloomLevel.B.RenderTarget);
                    _context.PixelShader.Set(_bloomBlurPixelShader);
                    _context.PixelShader.SetShaderResource(1, bloomLevel.A.ShaderResource);
                    _context.UpdateSubresource(ref constants, _postProcessBuffer);
                    _context.Draw(3, 0);
                    _context.PixelShader.SetShaderResource(1, null);

                    constants.PassParameters.X = 0;
                    constants.PassParameters.Y = 1;
                    _context.OutputMerger.SetTargets((DepthStencilView?)null, bloomLevel.A.RenderTarget);
                    _context.PixelShader.SetShaderResource(1, bloomLevel.B.ShaderResource);
                    _context.UpdateSubresource(ref constants, _postProcessBuffer);
                    _context.Draw(3, 0);
                    _context.PixelShader.SetShaderResource(1, null);
                }
            }
        }

        constants.PassParameters = Vector4.Zero;
        _context.Rasterizer.SetViewport(0, 0, _width, _height);
        _context.OutputMerger.SetTargets((DepthStencilView?)null, _renderTarget);
        _context.PixelShader.Set(_compositePixelShader);
        _context.PixelShader.SetShaderResource(0, _sceneShaderResource);
        for (var index = 0; index < _bloomLevels.Length; index++)
        {
            _context.PixelShader.SetShaderResource(index + 1, enabled ? _bloomLevels[index].A.ShaderResource : null);
        }
        _context.UpdateSubresource(ref constants, _postProcessBuffer);
        _context.Draw(3, 0);

        _context.PixelShader.SetShaderResources(0, new ShaderResourceView?[2]);
        _context.OutputMerger.SetDepthStencilState(null);
    }

    private TextureResource LoadMaterialTexture(string name, string fallback, bool useSrgb)
    {
        if (_materialTextureResolver?.Invoke(name) is { } custom)
        {
            return LoadTextureBytes($"custom:{custom.CacheKey}", custom.Contents, useSrgb);
        }

        if (_materialTextureResolver?.Invoke(fallback) is { } fallbackSource)
        {
            _missingTextures.Add(name);
            return LoadTextureBytes($"custom:{fallbackSource.CacheKey}", fallbackSource.Contents, useSrgb);
        }

        var normalized = string.IsNullOrWhiteSpace(name) ? fallback : name;
        var dot = normalized.LastIndexOf('.');
        if (dot >= 0)
        {
            normalized = normalized[(dot + 1)..];
        }
        if (!normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            normalized += ".png";
        }

        if (!File.Exists(Path.Combine(_resourceRoot, "Textures", normalized)))
        {
            _missingTextures.Add(name);
            normalized = fallback + ".png";
        }
        if (!File.Exists(Path.Combine(_resourceRoot, "Textures", normalized)))
        {
            normalized = "stars_bg.jpg";
        }
        return LoadTexture(normalized, useSrgb);
    }

    private TextureResource LoadResolvedTexture(string reference, string fallbackFileName, bool useSrgb)
    {
        if (_materialTextureResolver?.Invoke(reference) is { } source)
        {
            return LoadTextureBytes($"custom:{source.CacheKey}", source.Contents, useSrgb);
        }
        return LoadTexture(
            File.Exists(Path.Combine(_resourceRoot, "Textures", fallbackFileName))
                ? fallbackFileName
                : "stars_bg.jpg",
            useSrgb);
    }

    private TextureResource LoadTexture(string fileName, bool useSrgb)
    {
        var path = Path.Combine(_resourceRoot, "Textures", fileName);
        using var stream = File.OpenRead(path);
        return LoadTextureStream(fileName, stream, useSrgb);
    }

    private TextureResource LoadTextureBytes(string cacheName, byte[] contents, bool useSrgb)
    {
        using var stream = new MemoryStream(contents, writable: false);
        return LoadTextureStream(cacheName, stream, useSrgb);
    }

    private TextureResource LoadTextureStream(string cacheName, Stream stream, bool useSrgb)
    {
        var cacheKey = (cacheName, useSrgb);
        if (_textureCache.TryGetValue(cacheKey, out var cachedTexture))
        {
            return cachedTexture;
        }

        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var description = new Texture2DDescription
            {
                Width = converted.PixelWidth,
                Height = converted.PixelHeight,
                MipLevels = 0,
                ArraySize = 1,
                Format = useSrgb ? Format.B8G8R8A8_UNorm_SRgb : Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                OptionFlags = ResourceOptionFlags.GenerateMipMaps
            };
            var data = new DataBox(handle.AddrOfPinnedObject(), stride, 0);
            var texture = new Texture2D(_device, description);
            _context.UpdateSubresource(data, texture, 0);
            var view = new ShaderResourceView(_device, texture);
            _context.GenerateMips(view);
            var resource = new TextureResource(texture, view);
            _textureCache.Add(cacheKey, resource);
            return resource;
        }
        finally
        {
            handle.Free();
        }
    }

    private byte[] ReadTexture(byte[]? destination)
    {
        _context.CopyResource(_renderTexture, _stagingTexture);

        var data = _context.MapSubresource(_stagingTexture, 0, MapMode.Read, MapFlags.None);
        try
        {
            var pixels = destination ?? new byte[checked(_width * _height * 4)];
            for (var row = 0; row < _height; row++)
            {
                Marshal.Copy(IntPtr.Add(data.DataPointer, row * data.RowPitch), pixels, row * _width * 4, _width * 4);
            }

            return pixels;
        }
        finally
        {
            _context.UnmapSubresource(_stagingTexture, 0);
        }
    }

    private static ShaderBytecode CompileShader(string path, string entryPoint, string profile)
    {
        var result = ShaderBytecode.CompileFromFile(path, entryPoint, profile,
            ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness, EffectFlags.None);
        if (result.HasErrors)
        {
            throw new InvalidOperationException(result.Message);
        }
        return result.Bytecode;
    }

    private static Vector3 ConvertUnrealPositionToGltf(Vector3 value)
    {
        return new Vector3(value.X, value.Z, value.Y) / 100f;
    }

    private static Vector3 ConvertUnrealDirectionToGltf(Vector3 value)
    {
        return Vector3.Normalize(new Vector3(value.X, value.Z, value.Y));
    }

    private static Matrix ConvertUnrealRotationToGltf(Matrix unrealRotation)
    {
        var swapYAndZ = new Matrix(
            1, 0, 0, 0,
            0, 0, 1, 0,
            0, 1, 0, 0,
            0, 0, 0, 1);
        return swapYAndZ * unrealRotation * swapYAndZ;
    }

    private static Matrix CreateUnrealRotation(float pitchDegrees, float yawDegrees, float rollDegrees)
    {
        var pitch = MathUtil.DegreesToRadians(pitchDegrees);
        var yaw = MathUtil.DegreesToRadians(yawDegrees);
        var roll = MathUtil.DegreesToRadians(rollDegrees);
        var cp = MathF.Cos(pitch);
        var sp = MathF.Sin(pitch);
        var cy = MathF.Cos(yaw);
        var sy = MathF.Sin(yaw);
        var cr = MathF.Cos(roll);
        var sr = MathF.Sin(roll);

        return new Matrix(
            cp * cy, cp * sy, sp, 0,
            sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp, 0,
            -(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp, 0,
            0, 0, 0, 1);
    }

    public void Dispose()
    {
        _context.ClearState();
        _context.Flush();

        _coronaDepthState.Dispose();
        _backgroundDepthState.Dispose();
        _coronaBlendState.Dispose();
        _postProcessSampler.Dispose();
        _sampler.Dispose();
        _rasterizer.Dispose();
        DisposeRenderTargets();
        _postProcessBuffer.Dispose();
        _coronaMaterialBuffer.Dispose();
        _materialBuffer.Dispose();
        _sceneBuffer.Dispose();
        _coronaMesh.Dispose();
        _planetMesh.Dispose();
        _inputLayout.Dispose();
        _coronaPixelShader.Dispose();
        _coronaVertexShader.Dispose();
        _backgroundPixelShader.Dispose();
        _backgroundVertexShader.Dispose();
        _compositePixelShader.Dispose();
        _bloomBlurPixelShader.Dispose();
        _bloomExtractPixelShader.Dispose();
        _postProcessVertexShader.Dispose();
        _planetPixelShader.Dispose();
        _planetVertexShader.Dispose();
        foreach (var texture in _textureCache.Values)
        {
            texture.Dispose();
        }
        _textureCache.Clear();
        _context.Dispose();
        _device.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SceneConstants
    {
        public Matrix WorldViewProjection;
        public Matrix World;
        public Vector4 CameraPosition;
        public Vector4 RenderOptions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialConstants
    {
        public Vector4 CityEmissiveMixer;
        public Vector4 CityEmissiveColor;
        public Vector4 LandmassMixer;
        public Vector4 ContinentMaskMixer;
        public Vector4 ContinentMaskMixer02;
        public Vector4 ContinentColor;
        public Vector4 ContinentColorAlt;
        public Vector4 ContinentTextureMixer;
        public Vector4 BeachColor;
        public Vector4 OceanColor;
        public Vector4 OceanColorAlt;
        public Vector4 OceanTextureMixer;
        public Vector4 SiltColor;
        public Vector4 AtmosphereMixer;
        public Vector4 AtmosphereColor;
        public Vector4 HorizonAtmosphereColor;
        public Vector4 SkyLightColor;
        public Vector4 PointLight1PositionRadius;
        public Vector4 PointLight2PositionRadius;
        public Vector4 PointLight1Color;
        public Vector4 PointLight2Color;
        public Vector4 MaterialScalars0;
        public Vector4 MaterialScalars1;
        public Vector4 MaterialScalars2;
        public Vector4 MaterialScalars3;

        public static MaterialConstants From(
            PlanetRenderMaterial material,
            PreviewLightingSettings lighting,
            Vector3 light1Position,
            Vector3 light2Position) => new()
        {
            CityEmissiveMixer = ToDx(material.CityEmissiveMixer),
            CityEmissiveColor = ToDx(material.CityEmissiveColor),
            LandmassMixer = ToDx(material.LandmassMixer),
            ContinentMaskMixer = ToDx(material.ContinentMaskMixer),
            ContinentMaskMixer02 = ToDx(material.ContinentMaskMixer02),
            ContinentColor = ToDx(material.ContinentColor),
            ContinentColorAlt = ToDx(material.ContinentColorAlt),
            ContinentTextureMixer = ToDx(material.ContinentTextureMixer),
            BeachColor = ToDx(material.BeachColor),
            OceanColor = ToDx(material.OceanColor),
            OceanColorAlt = ToDx(material.OceanColorAlt),
            OceanTextureMixer = ToDx(material.OceanTextureMixer),
            SiltColor = ToDx(material.SiltColor),
            AtmosphereMixer = ToDx(material.AtmosphereMixer),
            AtmosphereColor = ToDx(material.AtmosphereColor),
            HorizonAtmosphereColor = ToDx(material.HorizonAtmosphereColor),
            SkyLightColor = lighting.SkyLightColor,
            PointLight1PositionRadius = new Vector4(
                light1Position,
                lighting.Light1.Radius / 100f),
            PointLight2PositionRadius = new Vector4(
                light2Position,
                lighting.Light2.Radius / 100f),
            PointLight1Color = UnpackArgbBytes(material.SunColor1),
            PointLight2Color = UnpackArgbBytes(material.SunColor2),
            MaterialScalars0 = new Vector4(
                material.NormalMapTile,
                material.BumpAmount,
                material.EmissiveTwinkleMultiplier,
                material.CityEmissiveTile),
            MaterialScalars1 = new Vector4(
                material.AtmosphereTileU,
                material.AtmosphereTileV,
                material.AtmospherePanMultiplier,
                material.AtmosphereMin),
            MaterialScalars2 = new Vector4(
                material.HorizonAtmosphereIntensity,
                material.HorizonAtmosphereFalloff,
                lighting.SkyLightBrightness,
                0),
            MaterialScalars3 = new Vector4(
                material.Brightness1,
                material.Brightness2,
                lighting.Light1.FalloffExponent,
                lighting.Light2.FalloffExponent)
        };

        private static Vector4 UnpackArgbBytes(uint packed) => new(
            (packed >> 16) & 0xff,
            (packed >> 8) & 0xff,
            packed & 0xff,
            (packed >> 24) & 0xff);

        private static Vector4 ToDx(System.Numerics.Vector4 value) =>
            new(value.X, value.Y, value.Z, value.W);
    }

    private static Vector4 ToDx(System.Numerics.Vector4 value) =>
        new(value.X, value.Y, value.Z, value.W);

    [StructLayout(LayoutKind.Sequential)]
    private struct CoronaMaterialConstants
    {
        public Vector4 CoronaColor;
        public Vector4 CoronaScalars;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PostProcessConstants
    {
        public Vector4 BloomParameters;
        public Vector4 PassParameters;
    }

    private sealed record PostProcessTarget(
        Texture2D Texture,
        RenderTargetView RenderTarget,
        ShaderResourceView ShaderResource) : IDisposable
    {
        public void Dispose()
        {
            ShaderResource.Dispose();
            RenderTarget.Dispose();
            Texture.Dispose();
        }
    }

    private sealed record BloomLevel(
        int Width,
        int Height,
        int BlurIterations,
        PostProcessTarget A,
        PostProcessTarget B) : IDisposable
    {
        public void Dispose()
        {
            B.Dispose();
            A.Dispose();
        }
    }

    private sealed record MeshBuffers(Buffer VertexBuffer, Buffer IndexBuffer, int IndexCount) : IDisposable
    {
        public void Dispose()
        {
            IndexBuffer.Dispose();
            VertexBuffer.Dispose();
        }
    }

    private sealed record TextureResource(Texture2D Texture, ShaderResourceView View) : IDisposable
    {
        public void Dispose()
        {
            View.Dispose();
            Texture.Dispose();
        }
    }
}
