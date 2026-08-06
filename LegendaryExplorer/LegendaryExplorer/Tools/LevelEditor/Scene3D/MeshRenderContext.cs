using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Resources;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Input;
using Color = System.Windows.Media.Color;
using D2D = SharpDX.Direct2D1;
using DW = SharpDX.DirectWrite;
using LECTexture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// A text label to be drawn at a screen-space position as a D2D overlay.
/// </summary>
public struct ScreenLabel(float x, float y, string text)
{
    public float X = x;
    public float Y = y;
    public string Text = text;
}

/// <summary>
/// Handles rendering of mesh data
/// </summary>
public class MeshRenderContext : RenderContext
{
    /// <summary>
    /// The current flags for rendering textures. This renderer does not support 'SetAlphaAsBlack' or 'ReconstructZ'
    /// </summary>
    public ShaderFlags RenderFlags = ShaderFlags.EnableRedChannel | ShaderFlags.EnableGreenChannel | ShaderFlags.EnableBlueChannel | ShaderFlags.EnableAlphaChannel;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WorldConstants
    {
        public Matrix4x4 Projection;
        public Matrix4x4 View;
        public Matrix4x4 Model;
        public Vector3 HitTestID;
        public ShaderFlags Flags;
        public Vector4 AmbientColor;
        public Fixed4<Vector4> LightPositionRadius;
        public Fixed4<Vector4> LightColorIntensity;
        public Fixed4<Vector4> LightDirectionInnerCone;
        public Fixed4<Vector4> LightOuterConeAndType;

        public WorldConstants(Matrix4x4 Projection, Matrix4x4 View, Matrix4x4 Model, ShaderFlags flags, Vector3 hitTestId)
        {
            this.Projection = Projection;
            this.View = View;
            this.Model = Model;
            this.Flags = flags;
            this.HitTestID = hitTestId;
            AmbientColor = new Vector4(0.2f, 0.2f, 0.2f, 1f);
            LightPositionRadius = default;
            LightColorIntensity = default;
            LightDirectionInnerCone = default;
            LightOuterConeAndType = default;
        }
    }

    [Flags]
    private enum KeyStates
    {
        None = 0,
        W = 0b1,
        A = 0b10,
        S = 0b100,
        D = 0b1000,
        Q = 0b10000,
        E = 0b100000,
        Up = 0b1000000,
        Down = 0b10000000,
        Left = 0b100000000,
        Right = 0b1000000000
    }

    public Color BackgroundColor = Color.FromArgb(255, 255, 255, 255); //Default

    #region Size-Dependent Resources
    public RenderTargetView BackbufferView { get; private set; }
    public Texture2D DepthBuffer { get; private set; } // also called Depth-Stencil, but we don't use stencil at the moment.
    public DepthStencilView DepthBufferView { get; private set; }
    protected Texture2D HitBuffer;
    protected RenderTargetView HitBufferView;
    private Texture2D PixelReadbackTexture;

    protected D2D.RenderTarget RenderTarget2D;
    private DW.TextFormat statsTextFormat;
    private DW.TextFormat errorTextFormat;
    private DW.TextFormat labelTextFormat;
    private D2D.SolidColorBrush statsTextBrush;
    private D2D.SolidColorBrush errorTextBrush;
    private D2D.SolidColorBrush labelTextBrush;
    private D2D.SolidColorBrush labelBackgroundBrush;
    #endregion
    public GenericEffect<WorldConstants> DefaultEffect { get; private set; }
    private GenericEffect<WorldConstants> NativeHitTestEffect;
    public LEEffect LEEffect { get; private set; }
    public LEEffect HumanLashEffect { get; private set; }
    private Texture2D DefaultTexture;
    private Texture2D WhiteTextureCube;
    private Texture2D WhiteTex;
    public ShaderResourceView DefaultTextureView { get; private set; }
    public ShaderResourceView WhiteTextureCubeView { get; private set; }
    public ShaderResourceView WhiteTexView { get; private set; }
    /// <summary>
    /// Optional scene-depth input for native material previews. Most preview tabs do not render a sampleable
    /// scene-depth buffer, so they leave this null. Specialized previews can provide a neutral or copied depth
    /// texture for materials that use UE3's depth-biased-alpha/soft-particle expressions.
    /// </summary>
    public virtual ShaderResourceView PreviewSceneDepthTextureView => null;
    private RasterizerState FillRasterizerState;
    private RasterizerState WireframeRasterizerState;
    private BlendState NativeHitTestBlendState;
    private DepthStencilState NativeHitTestDepthState;
    public SamplerState SampleState { get; private set; }
    private readonly Dictionary<(TextureAddressMode U, TextureAddressMode V), SamplerState> TextureSamplerCache = [];
    public readonly SceneCamera Camera = new();
    public SceneLightCollection SceneLights { get; }
    private bool wireframe;
    public bool Wireframe
    {
        get => wireframe;
        set
        {
            wireframe = value;
            if (Device != null)
            {
                if (wireframe)
                {
                    ImmediateContext.Rasterizer.State = WireframeRasterizerState;
                    RenderFlags |= ShaderFlags.Wireframe;
                }
                else
                {
                    ImmediateContext.Rasterizer.State = FillRasterizerState;
                    RenderFlags &= ~ShaderFlags.Wireframe;
                }
            }
        }
    }
    private KeyStates PressedKeys;
    private MouseButtons PressedMouseButton;
    protected bool HasActiveInput => PressedKeys is not KeyStates.None || PressedMouseButton is not MouseButtons.None;
    public float CameraSpeed { get; set; } = 500.0f; // Units per second
    public float Time { get; private set; }
    public uint NumFrames { get; private set; }

    private float FPS;
    private float lastFPSTime;
    private float lastFPSFrame;
    public string ErrorText;

    private static RawColor4 GetStatsTextColor()
    {
        return Settings.Global_DarkMode_Enabled ? new RawColor4(1, 1, 1, 1) : new RawColor4(0, 0, 0, 1);
    }

    /// <summary>
    /// Screen-space labels to be rendered as a D2D text overlay after 3D rendering.
    /// Populated by scene renderers, cleared each frame after drawing.
    /// </summary>
    public List<ScreenLabel> ScreenLabels { get; } = [];

    public Vector3 CurrentHitTestId;
    public uint CurrentLightingChannelMask;

    /// <summary>
    /// Native UE3 mesh shaders normally receive absolute level coordinates. Character actors can be
    /// far enough from the origin for those float values to collapse the separation between layered
    /// surfaces, so their component renderer enables camera-relative coordinates for the duration of a draw.
    /// </summary>
    internal bool UseCameraRelativeNativeRendering { get; set; }

    public event EventHandler<float> UpdateScene;
    public event EventHandler RenderScene;

    private readonly Dictionary<RenderTargetBlendDescription, BlendState> BlendStateCache = new(new BlendDescComparer());
    private readonly Dictionary<Guid, VertexShader> VertexShaderCache = [];
    private readonly Dictionary<Guid, InputLayout> InputLayoutCache = [];
    private readonly Dictionary<Guid, PixelShader> PixelShaderCache = [];
    private readonly Dictionary<Guid, PixelShader> NativePixelShaderCache = [];
    private readonly record struct MeshSourceKey(IMEPackage Package, int UIndex);
    private readonly record struct MeshGeometryKey(IMEPackage Package, int UIndex, Type VertexType, int LOD);
    private readonly record struct EntryReferenceKey(IMEPackage Package, int UIndex);
    private readonly record struct LightQueryKey(Vector3 Position, uint LightingChannelMask);
    private readonly Dictionary<MeshSourceKey, StaticMesh> StaticMeshCache = [];
    private readonly Dictionary<MeshSourceKey, SkeletalMesh> SkeletalMeshCache = [];
    private readonly Dictionary<EntryReferenceKey, ExportEntry> ResolvedExportCache = [];
    private readonly Dictionary<MeshGeometryKey, ISharedMeshData> MeshGeometryCache = [];
    private readonly Dictionary<LightQueryKey, SceneLight[]> NearestLightCache = [];
    private SceneLightSpatialIndex SceneLightIndex;
    public readonly PreviewTextureCache TextureCache;
    public readonly PackageCache PackageCache;

    public MeshRenderContext()
    {
        this.Camera.FocusDepth = 100.0f;
        SceneLights = new SceneLightCollection(InvalidateSceneLightCache);
        TextureCache = new PreviewTextureCache(this);
        PackageCache = new PackageCache();
    }

    internal StaticMesh GetCachedStaticMesh(ExportEntry export)
    {
        var key = new MeshSourceKey(export.FileRef, export.UIndex);
        if (!StaticMeshCache.TryGetValue(key, out StaticMesh mesh))
        {
            mesh = export.GetBinaryData<StaticMesh>();
            StaticMeshCache.Add(key, mesh);
        }
        return mesh;
    }

    /// <summary>
    /// Resolving an import may search the game's package directories. Character-heavy levels commonly
    /// reference the same outfit meshes and materials hundreds of times, so cache both successful and
    /// failed resolutions for the lifetime of the render context.
    /// </summary>
    internal ExportEntry ResolveExportCached(IMEPackage sourcePackage, int uIndex)
    {
        if (sourcePackage is null || uIndex == 0)
        {
            return null;
        }

        var key = new EntryReferenceKey(sourcePackage, uIndex);
        if (ResolvedExportCache.TryGetValue(key, out ExportEntry resolved))
        {
            return resolved;
        }

        resolved = sourcePackage.GetEntry(uIndex) switch
        {
            ExportEntry export => export,
            ImportEntry import => EntryImporter.ResolveImport(import, PackageCache),
            _ => null
        };
        ResolvedExportCache[key] = resolved;
        return resolved;
    }

    internal ExportEntry ResolveExportCached(IEntry entry) => entry switch
    {
        ExportEntry export => export,
        ImportEntry import => ResolveExportCached(import.FileRef, import.UIndex),
        _ => null
    };

    internal SkeletalMesh GetCachedSkeletalMesh(ExportEntry export)
    {
        var key = new MeshSourceKey(export.FileRef, export.UIndex);
        if (!SkeletalMeshCache.TryGetValue(key, out SkeletalMesh mesh))
        {
            mesh = export.GetBinaryData<SkeletalMesh>();
            SkeletalMeshCache.Add(key, mesh);
        }
        return mesh;
    }

    internal Mesh<TVertex> GetOrCreateCachedMesh<TVertex>(ExportEntry export, int lod,
        Func<(List<Triangle> Triangles, List<TVertex> Vertices)> createGeometry)
        where TVertex : IVertexBase
    {
        var key = new MeshGeometryKey(export.FileRef, export.UIndex, typeof(TVertex), lod);
        if (!MeshGeometryCache.TryGetValue(key, out ISharedMeshData cached))
        {
            (List<Triangle> triangles, List<TVertex> vertices) = createGeometry();
            cached = new SharedMeshData<TVertex>(Device, triangles, vertices);
            MeshGeometryCache.Add(key, cached);
        }
        return new Mesh<TVertex>((SharedMeshData<TVertex>)cached);
    }

    private void InvalidateSceneLightCache()
    {
        NearestLightCache.Clear();
        SceneLightIndex = null;
    }

    public override bool IsActivelyUpdating() => HasActiveInput || base.IsActivelyUpdating();

    public override void Update(float timestep)
    {
        Time += timestep;
        float fpsDelta = Time - lastFPSTime;
        if (fpsDelta >= 1f)
        {
            float frameDelta = NumFrames - lastFPSFrame;
            lastFPSTime = Time;
            lastFPSFrame = NumFrames;

            FPS = MathF.Round(frameDelta / fpsDelta);
        }

        if (Camera.IsOrthographic)
        {
            float panSpeed = Camera.OrthoWidth * 0.5f;
            if (PressedKeys.HasFlag(KeyStates.W))
                Camera.Position += Vector3.UnitY * timestep * panSpeed;
            if (PressedKeys.HasFlag(KeyStates.S))
                Camera.Position -= Vector3.UnitY * timestep * panSpeed;
            if (PressedKeys.HasFlag(KeyStates.A))
                Camera.Position -= Vector3.UnitX * timestep * panSpeed;
            if (PressedKeys.HasFlag(KeyStates.D))
                Camera.Position += Vector3.UnitX * timestep * panSpeed;
            if (PressedKeys.HasFlag(KeyStates.Q))
            {
                Camera.OrthoWidth *= 1 + timestep;
            }
            if (PressedKeys.HasFlag(KeyStates.E))
            {
                Camera.OrthoWidth *= 1 - timestep;
                Camera.OrthoWidth = MathF.Max(Camera.OrthoWidth, 1f);
            }
        }
        else if (Camera.FirstPerson)
        {
            if (PressedKeys.HasFlag(KeyStates.W))
            {
                Camera.Position += Camera.CameraForward * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.S))
            {
                Camera.Position -= Camera.CameraForward * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.A))
            {
                Camera.Position -= Camera.CameraRight * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.D))
            {
                Camera.Position += Camera.CameraRight * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.Q))
            {
                Camera.Position -= Vector3.UnitZ * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.E))
            {
                Camera.Position += Vector3.UnitZ * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.Up))
            {
                Camera.Position += Vector3.UnitZ * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.Down))
            {
                Camera.Position -= Vector3.UnitZ * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.Left))
            {
                Camera.Yaw -= timestep * 1.5f;
            }
            if (PressedKeys.HasFlag(KeyStates.Right))
            {
                Camera.Yaw += timestep * 1.5f;
            }
        }

        UpdateScene?.Invoke(null, timestep);
    }

    public override void Render()
    {
        NumFrames++;
        // Clear the color and depth buffers
        if (BackbufferView != null)
        {
            ClearDepthBuffer();
            ImmediateContext.ClearRenderTargetView(BackbufferView, new RawColor4(BackgroundColor.R / 255.0f, BackgroundColor.G / 255.0f, BackgroundColor.B / 255.0f, BackgroundColor.A / 255.0f));
            if (HitBufferView is not null) ImmediateContext.ClearRenderTargetView(HitBufferView, new RawColor4(1f, 1f, 1f, 1f));

            if (ErrorText is not null)
            {
                RenderTarget2D.BeginDraw();
                {
                    var size = RenderTarget2D.Size;
                    RenderTarget2D.DrawText($"{ErrorText}", errorTextFormat, new RawRectangleF(0, 0, size.Width, size.Height), errorTextBrush);
                }
                RenderTarget2D.EndDraw();
            }
            else
            {
                try
                {
                    RenderScene?.Invoke(null, EventArgs.Empty);
                }
                catch (Exception e)
                {
                    ErrorText = e.FlattenException();
                }
            }

            //render D2D overlay
            RenderTarget2D.BeginDraw();
            {
                if (App.IsDebug)
                {
                    var size = RenderTarget2D.Size;
                    statsTextBrush.Color = GetStatsTextColor();
                    RenderTarget2D.DrawText($"{FPS} fps\n{Camera.Position}", statsTextFormat, new RawRectangleF(0, 0, size.Width, size.Height), statsTextBrush);
                }

                foreach (ref readonly var label in CollectionsMarshal.AsSpan(ScreenLabels))
                {
                    float labelW = MathF.Max(20f, (label.Text?.Length ?? 0) * 6.5f + 10f);
                    const float labelH = 14f;
                    var rect = new RawRectangleF(label.X - labelW * 0.5f, label.Y - labelH * 0.5f,
                                                 label.X + labelW * 0.5f, label.Y + labelH * 0.5f);
                    RenderTarget2D.FillRectangle(rect, labelBackgroundBrush);
                    RenderTarget2D.DrawText(label.Text, labelTextFormat, rect, labelTextBrush);
                }
                ScreenLabels.Clear();
            }
            RenderTarget2D.EndDraw();
        }

        base.Render();
    }

    public void ClearDepthBuffer()
    {
        if (DepthBufferView != null)
        {
            ImmediateContext.ClearDepthStencilView(DepthBufferView, DepthStencilClearFlags.Depth, 1.0f, 0);
        }
    }

    public override void CreateResources()
    {
        base.CreateResources();

        // Build a custom rasterizer state that doesn't cull backfaces
        var frs = new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid
        };
        FillRasterizerState = new RasterizerState(Device, frs);
        ImmediateContext.Rasterizer.State = FillRasterizerState;
        // Build a custom rasterizer state for wireframe drawing
        var wrs = new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Wireframe,
            IsAntialiasedLineEnabled = false,
            DepthBias = -10
        };
        WireframeRasterizerState = new RasterizerState(Device, wrs);

        // Set texture sampler state
        SampleState = new SamplerState(Device, CreateSamplerDescription(TextureAddressMode.Wrap, TextureAddressMode.Wrap));
        ResetTextureSamplers();

        // Load the default texture
        DefaultTexture = this.LoadTextureFromFile(Path.Combine(AppDirectories.ExecFolder, "Default.png"));
        DefaultTextureView = new ShaderResourceView(Device, DefaultTexture);

        // Load the default position-texture shader
        DefaultEffect = new GenericEffect<WorldConstants>(Device, EmbeddedResources.LevelEditorShader);
        NativeHitTestEffect = new GenericEffect<WorldConstants>(
            Device,
            EmbeddedResources.LevelEditorNativeHitTestShader,
            [new InputElement("POSITION", 0, Format.R32G32B32A32_Float, 0)]);
        NativeHitTestBlendState = new BlendState(Device, new BlendStateDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = true,
            RenderTarget =
            {
                [0] = new RenderTargetBlendDescription
                {
                    RenderTargetWriteMask = ColorWriteMaskFlags.All,
                    BlendOperation = BlendOperation.Add,
                    AlphaBlendOperation = BlendOperation.Add,
                    SourceBlend = BlendOption.SourceAlpha,
                    DestinationBlend = BlendOption.InverseSourceAlpha,
                    SourceAlphaBlend = BlendOption.One,
                    DestinationAlphaBlend = BlendOption.InverseSourceAlpha,
                    IsBlendEnabled = true
                },
                [1] = new RenderTargetBlendDescription
                {
                    RenderTargetWriteMask = ColorWriteMaskFlags.All,
                    BlendOperation = BlendOperation.Add,
                    AlphaBlendOperation = BlendOperation.Add,
                    SourceBlend = BlendOption.One,
                    DestinationBlend = BlendOption.Zero,
                    SourceAlphaBlend = BlendOption.One,
                    DestinationAlphaBlend = BlendOption.Zero,
                    IsBlendEnabled = false
                }
            }
        });
        NativeHitTestDepthState = new DepthStencilState(Device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.LessEqual,
            IsStencilEnabled = false
        });

        //create fallback textures
        var whiteCubeData = new Fixed6<byte[]>();
        whiteCubeData[0] = whiteCubeData[1] = whiteCubeData[2] = whiteCubeData[3] = whiteCubeData[4] = whiteCubeData[5] = [255, 255, 255, 255];
        WhiteTextureCube = this.LoadTextureCube(1, Format.R8G8B8A8_UNorm, whiteCubeData);
        WhiteTextureCubeView = new ShaderResourceView(Device, WhiteTextureCube);
        WhiteTex = new Texture2D(Device, new Texture2DDescription { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm, SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.ShaderResource });
        int white = int.MaxValue;
        Device.ImmediateContext.UpdateSubresource(ref white, WhiteTex, rowPitch: 8);
        WhiteTexView = new ShaderResourceView(Device, WhiteTex);

        LEEffect = new LEEffect(Device);
        HumanLashEffect = new LEEffect(Device);
    }

    public SamplerState GetTextureSampler(PreviewTextureCache.TextureEntry texture)
    {
        if (texture is null || (texture.AddressU == TextureAddressMode.Wrap && texture.AddressV == TextureAddressMode.Wrap))
        {
            return SampleState;
        }

        var key = (U: texture.AddressU, V: texture.AddressV);
        if (!TextureSamplerCache.TryGetValue(key, out SamplerState sampler))
        {
            sampler = new SamplerState(Device, CreateSamplerDescription(key.U, key.V));
            TextureSamplerCache.Add(key, sampler);
        }
        return sampler;
    }

    public void ResetTextureSamplers()
    {
        // Most preview effects rely on the renderer's default wrap sampler. Compiled game
        // materials temporarily replace individual slots with their texture's address mode.
        const int numSampleStates = 16;
        for (int i = 0; i < numSampleStates; i++)
        {
            ImmediateContext.PixelShader.SetSampler(i, SampleState);
        }
    }

    private static SamplerStateDescription CreateSamplerDescription(TextureAddressMode addressU, TextureAddressMode addressV)
        => new()
        {
            AddressU = addressU,
            AddressV = addressV,
            AddressW = TextureAddressMode.Wrap,
            Filter = Filter.Anisotropic,
            MaximumAnisotropy = 8
        };

    public override void CreateSizeDependentResources(int width, int height, Texture2D newBackBuffer)
    {
        base.CreateSizeDependentResources(width, height, newBackBuffer);
        BackbufferView = new RenderTargetView(Device, Backbuffer);
        PixelReadbackTexture = new Texture2D(Device, new Texture2DDescription
        {
            ArraySize = 1,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            Format = Backbuffer.Description.Format,
            Height = 3,
            Width = 3,
            MipLevels = 1,
            OptionFlags = ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging
        });
        DepthBuffer = new Texture2D(Device, new Texture2DDescription
        {
            ArraySize = 1,
            BindFlags = BindFlags.DepthStencil,
            CpuAccessFlags = CpuAccessFlags.None,
            Format = Format.D32_Float,
            Height = Height,
            Width = Width,
            MipLevels = 1,
            OptionFlags = ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default
        });
        DepthBufferView = new DepthStencilView(Device, DepthBuffer);

        HitBuffer = new Texture2D(Device, new Texture2DDescription
        {
            ArraySize = 1,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            Format = Format.B8G8R8A8_UNorm,
            Height = Height,
            Width = Width,
            MipLevels = 1,
            OptionFlags = ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default
        });
        HitBufferView = new RenderTargetView(Device, HitBuffer);

        ImmediateContext.OutputMerger.SetRenderTargets(DepthBufferView, BackbufferView, HitBufferView);
        ImmediateContext.Rasterizer.SetViewport(0, 0, Width, Height);

        Camera.aspect = (float)Width / Height;


        using var factory = new D2D.Factory(D2D.FactoryType.SingleThreaded, App.IsDebug ? D2D.DebugLevel.Information : D2D.DebugLevel.None);
        RenderTarget2D = new D2D.RenderTarget(factory, newBackBuffer.QueryInterface<Surface>(), new D2D.RenderTargetProperties(new D2D.PixelFormat(Format.Unknown, D2D.AlphaMode.Premultiplied)));
        statsTextBrush = new D2D.SolidColorBrush(RenderTarget2D, GetStatsTextColor(), new D2D.BrushProperties { Opacity = 1 });
        errorTextBrush = new D2D.SolidColorBrush(RenderTarget2D, new RawColor4(0.2f, 0, 0, 1), new D2D.BrushProperties { Opacity = 1 });
        using var dwFactory = new DW.Factory(DW.FactoryType.Shared);
        statsTextFormat = new DW.TextFormat(dwFactory, "Verdana", 12)
        {
            TextAlignment = DW.TextAlignment.Trailing,
            ParagraphAlignment = DW.ParagraphAlignment.Near
        };
        errorTextFormat = new DW.TextFormat(dwFactory, "Verdana", 18)
        {
            TextAlignment = DW.TextAlignment.Leading,
            ParagraphAlignment = DW.ParagraphAlignment.Center
        };
        labelTextFormat = new DW.TextFormat(dwFactory, "Verdana", 8)
        {
            TextAlignment = DW.TextAlignment.Center,
            ParagraphAlignment = DW.ParagraphAlignment.Center
        };
        labelTextBrush = new D2D.SolidColorBrush(RenderTarget2D, new RawColor4(1, 1, 1, 1), new D2D.BrushProperties { Opacity = 1 });
        labelBackgroundBrush = new D2D.SolidColorBrush(RenderTarget2D, new RawColor4(0, 0, 0, 0.65f), new D2D.BrushProperties { Opacity = 1 });
    }

    /// <summary>
    /// Reads the average color of the 3x3 backbuffer region centered on a pixel.
    /// Used by live material parameter picking.
    /// </summary>
    public unsafe bool TryReadBackbufferPixelNeighborhood(int x, int y, out Vector4 color)
    {
        color = default;
        if (PixelReadbackTexture is null || Backbuffer is null || Width <= 0 || Height <= 0)
        {
            return false;
        }

        int minX = Math.Max(x - 1, 0);
        int maxX = Math.Min(x + 1, Width - 1);
        int minY = Math.Max(y - 1, 0);
        int maxY = Math.Min(y + 1, Height - 1);
        int sampleWidth = maxX - minX + 1;
        int sampleHeight = maxY - minY + 1;

        Format format = Backbuffer.Description.Format;
        bool isBgra = format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb;
        bool isRgba = format is Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb;
        if (!isBgra && !isRgba)
        {
            return false;
        }

        ImmediateContext.CopySubresourceRegion(Backbuffer, 0,
            new ResourceRegion(minX, minY, 0, maxX + 1, maxY + 1, 1), PixelReadbackTexture, 0);
        SharpDX.DataBox mapped = ImmediateContext.MapSubresource(
            PixelReadbackTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            Vector4 sum = default;
            for (int sampleY = 0; sampleY < sampleHeight; sampleY++)
            {
                var row = new Span<SharpDX.Color>(
                    (mapped.DataPointer + sampleY * mapped.RowPitch).ToPointer(), sampleWidth);
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
            ImmediateContext.UnmapSubresource(PixelReadbackTexture, 0);
        }
    }

    public override void DisposeSizeDependentResources()
    {
        ImmediateContext.OutputMerger.SetRenderTargets((RenderTargetView)null);
        PixelReadbackTexture?.Dispose();
        PixelReadbackTexture = null;
        BackbufferView.Dispose();
        BackbufferView = null;
        DepthBufferView.Dispose();
        DepthBufferView = null;
        DepthBuffer.Dispose();
        DepthBuffer = null;
        HitBufferView?.Dispose();
        HitBufferView = null;
        HitBuffer?.Dispose();
        HitBuffer = null;
        RenderTarget2D.Dispose();
        statsTextFormat?.Dispose();
        errorTextFormat?.Dispose();
        labelTextFormat?.Dispose();
        statsTextBrush?.Dispose();
        errorTextBrush?.Dispose();
        labelTextBrush?.Dispose();
        labelBackgroundBrush?.Dispose();
        base.DisposeSizeDependentResources();
    }

    public override void DisposeResources()
    {
        if (!IsReady)
            return;

        TextureCache?.Dispose();
        DefaultTextureView?.Dispose();
        WhiteTextureCubeView?.Dispose();
        WhiteTexView?.Dispose();
        DefaultTexture?.Dispose();
        WhiteTextureCube?.Dispose();
        WhiteTex?.Dispose();
        foreach (SamplerState sampler in TextureSamplerCache.Values)
        {
            sampler.Dispose();
        }
        TextureSamplerCache.Clear();
        SampleState?.Dispose();
        DefaultEffect?.Dispose();
        NativeHitTestEffect?.Dispose();
        NativeHitTestBlendState?.Dispose();
        NativeHitTestDepthState?.Dispose();
        LEEffect?.Dispose();
        HumanLashEffect?.Dispose();
        FillRasterizerState?.Dispose();
        WireframeRasterizerState?.Dispose();
        EmptyCaches();
        base.DisposeResources();
    }

    public void RenderMeshAsWireframe(Mesh<WorldVertex> mesh)
    {
        bool wireframeBackup = Wireframe;
        Wireframe = true;
        DefaultEffect.PrepDraw(ImmediateContext, AlphaBlendState, GetWorldConstants(mesh.LocalToWorld));
        DefaultEffect.RenderObject(ImmediateContext, mesh, null);
        Wireframe = wireframeBackup;
    }

    public void RenderMeshAsWireframe(Mesh<WorldVertex> mesh, ModelPreviewSection section)
    {
        bool wireframeBackup = Wireframe;
        Wireframe = true;
        DefaultEffect.PrepDraw(ImmediateContext, AlphaBlendState, GetWorldConstants(mesh.LocalToWorld));
        DefaultEffect.RenderObject(ImmediateContext, mesh, (int)section.StartIndex, (int)section.TriangleCount * 3, null);
        Wireframe = wireframeBackup;
    }

    public WorldConstants GetWorldConstants(Matrix4x4 localToWorld)
    {
        WorldConstants constants = new(Matrix4x4.Transpose(Camera.ProjectionMatrix), Matrix4x4.Transpose(Camera.ViewMatrix), Matrix4x4.Transpose(localToWorld), RenderFlags, CurrentHitTestId);

        Vector3 objectPosition = localToWorld.Translation;
        uint meshMask = CurrentLightingChannelMask;
        var lightQuery = new LightQueryKey(objectPosition, meshMask);
        if (!NearestLightCache.TryGetValue(lightQuery, out SceneLight[] nearestLights))
        {
            SceneLightIndex ??= new SceneLightSpatialIndex(SceneLights);
            nearestLights = SceneLightIndex.FindNearest(objectPosition, meshMask);
            if (NearestLightCache.Count >= 16_384) NearestLightCache.Clear();
            NearestLightCache[lightQuery] = nearestLights;
        }

        for (int slot = 0; slot < nearestLights.Length; slot++)
        {
            SceneLight light = nearestLights[slot];
            constants.LightPositionRadius[slot] = new Vector4(light.Position, light.Radius);
            constants.LightColorIntensity[slot] = new Vector4(light.Color, light.Intensity);
            constants.LightDirectionInnerCone[slot] = new Vector4(light.Direction, light.InnerConeCos);
            constants.LightOuterConeAndType[slot] = new Vector4(light.OuterConeCos, light.IsSpot ? 1f : 0f, 0f, 0f);
        }

        return constants;
    }

    internal Matrix4x4 GetNativeShaderViewMatrix()
    {
        Matrix4x4 view = Camera.ViewMatrix;
        if (UseCameraRelativeNativeRendering)
        {
            view.Translation = Vector3.Zero;
        }
        return view;
    }

    internal Matrix4x4 GetNativeShaderLocalToWorld(Matrix4x4 localToWorld)
    {
        if (UseCameraRelativeNativeRendering)
        {
            localToWorld.Translation -= Camera.Position;
        }
        return localToWorld;
    }

    internal Vector3 GetNativeShaderCameraPosition() =>
        UseCameraRelativeNativeRendering ? Vector3.Zero : Camera.Position;

    internal Vector3 GetNativeShaderWorldPosition(Vector3 worldPosition) =>
        UseCameraRelativeNativeRendering ? worldPosition - Camera.Position : worldPosition;

    internal void RenderNativeMeshHitTest(Mesh<LEVertex> mesh)
    {
        if (mesh?.Vertices.Count is not > 0 || mesh.Triangles.Count == 0)
        {
            return;
        }

        bool previousCameraRelative = UseCameraRelativeNativeRendering;
        UseCameraRelativeNativeRendering = true;
        try
        {
            WorldConstants constants = new(
                Matrix4x4.Transpose(Camera.ProjectionMatrix),
                Matrix4x4.Transpose(GetNativeShaderViewMatrix()),
                Matrix4x4.Transpose(GetNativeShaderLocalToWorld(mesh.LocalToWorld)),
                RenderFlags,
                CurrentHitTestId);
            ImmediateContext.OutputMerger.SetDepthStencilState(NativeHitTestDepthState);
            NativeHitTestEffect.PrepDraw(ImmediateContext, NativeHitTestBlendState, constants);
            NativeHitTestEffect.RenderObjectWithLayout(
                ImmediateContext, mesh, 0, mesh.Triangles.Count * 3);
        }
        finally
        {
            ImmediateContext.OutputMerger.SetDepthStencilState(null);
            UseCameraRelativeNativeRendering = previousCameraRelative;
        }
    }

    public BlendState GetCachedBlendState(RenderTargetBlendDescription renderTargetBlendDesc)
    {
        if (!BlendStateCache.TryGetValue(renderTargetBlendDesc, out BlendState blendState))
        {
            blendState = new BlendState(Device, new BlendStateDescription
            {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = true,
                RenderTarget =
                {
                    [0] = renderTargetBlendDesc
                }
            });
            BlendStateCache.Add(renderTargetBlendDesc, blendState);
        }
        return blendState;
    }

    public (VertexShader, InputLayout) GetCachedVertexShader(Guid id, byte[] shaderBytecode)
        => GetCachedVertexShader<LEVertex>(id, shaderBytecode);

    public (VertexShader, InputLayout) GetCachedVertexShader<TVertex>(Guid id, byte[] shaderBytecode)
        where TVertex : IVertexBase
    {
        InputLayout inputLayout;
        if (VertexShaderCache.TryGetValue(id, out VertexShader shader))
        {
            inputLayout = InputLayoutCache[id];
        }
        else
        {
            shader = new VertexShader(Device, shaderBytecode);
            VertexShaderCache.Add(id, shader);
            inputLayout = new InputLayout(Device, shaderBytecode, TVertex.InputElements);
            InputLayoutCache.Add(id, inputLayout);
        }
        return (shader, inputLayout);
    }

    /// <summary>
    /// Verifies the complete reflected vertex-shader input contract before a native vertex factory is enabled.
    /// D3D permits a supplied element to contain more components than a shader consumes, but never fewer.
    /// </summary>
    public static bool ValidateVertexShaderInputLayout<TVertex>(byte[] shaderBytecode, out string error)
        where TVertex : IVertexBase
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
            InputElement? matchingElement = null;
            foreach (InputElement element in TVertex.InputElements)
            {
                if (element.SemanticIndex == input.SemanticIndex
                    && string.Equals(element.SemanticName, input.SemanticName, StringComparison.OrdinalIgnoreCase))
                {
                    matchingElement = element;
                    break;
                }
            }
            if (matchingElement is null)
            {
                error = $"The {typeof(TVertex).Name} layout does not provide {input.SemanticName}{input.SemanticIndex}.";
                return false;
            }

            int requiredComponents = HighestSetBit((int)input.UsageMask);
            int suppliedComponents = GetFormatComponentCount(matchingElement.Value.Format);
            if (suppliedComponents < requiredComponents)
            {
                error = $"{input.SemanticName}{input.SemanticIndex} requires {requiredComponents} components, "
                    + $"but {typeof(TVertex).Name} provides {suppliedComponents}.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Validates both sides of a native vertex-factory contract. The canonical requirements are checked even
    /// when a particular material shader optimized an unused input away, then the compiled shader signature is
    /// checked as a second guard against game- or material-specific additions.
    /// </summary>
    public static bool ValidateVertexFactoryInputLayout<TVertex>(
        string vertexFactoryType,
        byte[] shaderBytecode,
        out string error)
        where TVertex : IVertexBase
    {
        (string Semantic, int Index, int Components)[] requiredInputs = vertexFactoryType switch
        {
            "FLocalVertexFactory" =>
            [
                ("POSITION", 0, 4), ("TANGENT", 0, 3), ("NORMAL", 0, 4), ("COLOR", 1, 4),
                ("TEXCOORD", 0, 4), ("TEXCOORD", 1, 4), ("TEXCOORD", 2, 4), ("TEXCOORD", 3, 4)
            ],
            "FParticleVertexFactory" => ParticleBaseInputs(),
            "FParticleSubUVVertexFactory" => [.. ParticleBaseInputs(), ("TEXCOORD", 2, 4)],
            "FParticleDynamicParameterVertexFactory" => [.. ParticleBaseInputs(), ("TEXCOORD", 3, 4)],
            "FParticleSubUVDynamicParameterVertexFactory" =>
                [.. ParticleBaseInputs(), ("TEXCOORD", 2, 4), ("TEXCOORD", 3, 4)],
            "FParticleBeamTrailVertexFactory" => BeamTrailBaseInputs(),
            "FParticleBeamTrailDynamicParameterVertexFactory" =>
                [.. BeamTrailBaseInputs(), ("TEXCOORD", 2, 4)],
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
                error = $"The {typeof(TVertex).Name} layout does not provide the {vertexFactoryType} input "
                    + $"{semantic}{semanticIndex}.";
                return false;
            }
            int suppliedComponents = GetFormatComponentCount(matchingElement.Value.Format);
            if (suppliedComponents < requiredComponents)
            {
                error = $"The {vertexFactoryType} input {semantic}{semanticIndex} requires {requiredComponents} "
                    + $"components, but {typeof(TVertex).Name} provides {suppliedComponents}.";
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

    private static InputElement? FindInputElement<TVertex>(string semantic, int semanticIndex)
        where TVertex : IVertexBase
    {
        foreach (InputElement element in TVertex.InputElements)
        {
            if (element.SemanticIndex == semanticIndex
                && string.Equals(element.SemanticName, semantic, StringComparison.OrdinalIgnoreCase))
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
        Format.R32G32_Float or Format.R32G32_UInt or Format.R32G32_SInt => 2,
        Format.R32G32B32_Float or Format.R32G32B32_UInt or Format.R32G32B32_SInt => 3,
        Format.R32G32B32A32_Float or Format.R32G32B32A32_UInt or Format.R32G32B32A32_SInt => 4,
        _ => 0
    };

    public PixelShader GetCachedPixelShader(Guid id, byte[] shaderBytecode)
    {
        if (!PixelShaderCache.TryGetValue(id, out PixelShader shader))
        {
            string code = HLSLDecompiler.DecompileShader(shaderBytecode, false);
            //HACK: LE shaders seem to always output pixels with no alpha (Maybe it's inverted? Investigate transparent mats) 
            code = code.Replace("o0.w = 0;", "o0.w = 1;", StringComparison.Ordinal);
            //3DMigoto outputs "inf" for the infinity constant, but that's not valid HLSL
            code = code.Replace("// 3Dmigoto declarations", "// 3Dmigoto declarations\n" +
                                                            "#define inf 1.#INF");
            try
            {
                shaderBytecode = ShaderBytecode.Compile(code, "main", "ps_5_0");
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
            }

            shader = new PixelShader(Device, shaderBytecode);
            PixelShaderCache.Add(id, shader);
        }
        return shader;
    }

    /// <summary>
    /// Returns the cooked pixel shader byte-for-byte. The legacy mesh preview recompiles shaders and forces an
    /// alpha of one for opaque model inspection; particle blending must retain the authored output alpha.
    /// </summary>
    public PixelShader GetCachedNativePixelShader(Guid id, byte[] shaderBytecode)
    {
        if (!NativePixelShaderCache.TryGetValue(id, out PixelShader shader))
        {
            shader = new PixelShader(Device, shaderBytecode);
            NativePixelShaderCache.Add(id, shader);
        }
        return shader;
    }

    public override void EmptyCaches()
    {
        foreach (ISharedMeshData geometry in MeshGeometryCache.Values)
        {
            geometry.ReleaseReference();
        }
        MeshGeometryCache.Clear();
        StaticMeshCache.Clear();
        SkeletalMeshCache.Clear();
        ResolvedExportCache.Clear();
        NearestLightCache.Clear();
        SceneLightIndex = null;
        PackageCache?.ReleasePackages();
        TextureCache?.ExpungeStaleCacheItems();
        BlendStateCache.DisposeValuesAndClear();
        VertexShaderCache.DisposeValuesAndClear();
        InputLayoutCache.DisposeValuesAndClear();
        PixelShaderCache.DisposeValuesAndClear();
        NativePixelShaderCache.DisposeValuesAndClear();
    }

    private System.Drawing.Point mouseDownPos;
    public override bool MouseDown(MouseButtons button, int x, int y)
    {
        if (PressedMouseButton is MouseButtons.None)
        {
            mouseDownPos = new System.Drawing.Point(x, y);
            PressedMouseButton = button;
        }
        return false;
    }

    public override bool MouseUp(MouseButtons button, int x, int y)
    {
        PressedMouseButton = MouseButtons.None;

        //if it moved any significant amount, we count it as a drag
        return Math.Abs(x - mouseDownPos.X) > 3 || Math.Abs(y - mouseDownPos.Y) > 3;
    }

    private System.Drawing.Point lastMouse;
    public override bool MouseMove(int x, int y)
    {
        bool handled = false;
        int xDiff = (x - lastMouse.X);
        int yDiff = (y - lastMouse.Y);
        if (Camera.IsOrthographic)
        {
            switch (PressedMouseButton)
            {
                case MouseButtons.Left:
                case MouseButtons.Middle:
                    float worldPerPixel = Camera.OrthoWidth / Width;
                    Camera.Position += new Vector3(-xDiff * worldPerPixel, yDiff * worldPerPixel, 0);
                    handled = true;
                    break;
                case MouseButtons.Right:
                    Camera.OrthoWidth *= MathF.Pow(1.01f, yDiff);
                    Camera.OrthoWidth = MathF.Max(Camera.OrthoWidth, 1f);
                    handled = true;
                    break;
            }
        }
        else if (Camera.FirstPerson)
        {
            switch (PressedMouseButton)
            {
                case MouseButtons.Left:
                    var camFwd = (Camera.CameraForward with { Z = 0 }).Normal();
                    Camera.Position += camFwd * -yDiff * (CameraSpeed / FPS);
                    Camera.Yaw += xDiff * 0.01f;
                    handled = true;
                    break;
                case MouseButtons.Middle:
                    Camera.Position += Camera.CameraRight * -xDiff * (CameraSpeed / FPS);
                    Camera.Position += Camera.CameraUp * yDiff * (CameraSpeed / FPS);
                    handled = true;
                    break;
                case MouseButtons.Right:
                    Camera.Yaw += xDiff * 0.01f;
                    Camera.Pitch = (Camera.Pitch - yDiff * 0.01f).Clamp(-MathF.PI / 2 + 0.01f, MathF.PI / 2 - 0.01f);
                    handled = true;
                    break;
            }
        }
        else
        {
            switch (PressedMouseButton)
            {
                //orbiting
                case MouseButtons.Left:
                    Camera.Yaw += xDiff * 0.01f;
                    Camera.Pitch = (Camera.Pitch - yDiff * 0.01f).Clamp(-MathF.PI / 2 + 0.01f, MathF.PI / 2 - 0.01f);
                    handled = true;
                    break;
                //panning
                case MouseButtons.Middle:
                    Camera.Position -= Camera.CameraRight * xDiff * Camera.FocusDepth * 0.004f;
                    Camera.Position += Camera.CameraUp * yDiff * Camera.FocusDepth * 0.004f;
                    handled = true;
                    break;
                //zooming
                case MouseButtons.Right:
                    Camera.FocusDepth += yDiff * Camera.FocusDepth * 0.1f * 0.1f;
                    if (Camera.FocusDepth < 0.1) Camera.FocusDepth = 0.1f;
                    handled = true;
                    break;
            }
        }
        lastMouse = new System.Drawing.Point(x, y);
        return handled;
    }

    public override bool MouseScroll(int delta)
    {
        if (Camera.IsOrthographic)
        {
            Camera.OrthoWidth *= MathF.Pow(1.2f, -Math.Sign(delta));
            Camera.OrthoWidth = MathF.Max(Camera.OrthoWidth, 1f);
        }
        else if (Camera.FirstPerson)
        {
            Camera.Position += Camera.CameraForward * (CameraSpeed / FPS) * (delta / 10f);
        }
        else
        {
            Camera.FocusDepth *= MathF.Pow(1.2f, -Math.Sign(delta)); // kinda hacky because this moves in constant increments regardless of how far the user scrolls.
        }
        return true;
    }

    /// <summary>
    /// Handles key down events. Returns true if the key was accepted.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public override bool KeyDown(Key key)
    {
        switch (key)
        {
            case Key.W:
                PressedKeys |= KeyStates.W;
                return true;
            case Key.S:
                PressedKeys |= KeyStates.S;
                return true;
            case Key.A:
                PressedKeys |= KeyStates.A;
                return true;
            case Key.D:
                PressedKeys |= KeyStates.D;
                return true;
            case Key.Q:
                PressedKeys |= KeyStates.Q;
                return true;
            case Key.E:
                PressedKeys |= KeyStates.E;
                return true;
            case Key.Up:
                PressedKeys |= KeyStates.Up;
                return true;
            case Key.Down:
                PressedKeys |= KeyStates.Down;
                return true;
            case Key.Left:
                PressedKeys |= KeyStates.Left;
                return true;
            case Key.Right:
                PressedKeys |= KeyStates.Right;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Handles key up events. Returns true if the key was accepted.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public override bool KeyUp(Key key)
    {
        switch (key)
        {
            case Key.W:
                PressedKeys &= ~KeyStates.W;
                return true;
            case Key.S:
                PressedKeys &= ~KeyStates.S;
                return true;
            case Key.A:
                PressedKeys &= ~KeyStates.A;
                return true;
            case Key.D:
                PressedKeys &= ~KeyStates.D;
                return true;
            case Key.Q:
                PressedKeys &= ~KeyStates.Q;
                return true;
            case Key.E:
                PressedKeys &= ~KeyStates.E;
                return true;
            case Key.Up:
                PressedKeys &= ~KeyStates.Up;
                return true;
            case Key.Down:
                PressedKeys &= ~KeyStates.Down;
                return true;
            case Key.Left:
                PressedKeys &= ~KeyStates.Left;
                return true;
            case Key.Right:
                PressedKeys &= ~KeyStates.Right;
                return true;
            default:
                return false;
        }
    }

    public override bool LostKeyboardFocus()
    {
        bool handled = PressedKeys is not KeyStates.None;

        PressedKeys = KeyStates.None;

        return handled;
    }

    public override bool LostMouseFocus()
    {
        bool handled = PressedMouseButton is not MouseButtons.None;

        PressedMouseButton = MouseButtons.None;

        return handled;
    }

    public Vector4 WorldToScreen(Vector3 point)
    {
        return Vector4.Transform(point, Camera.ViewProjectionMatrix);
    }

    /// <summary>
    /// Conservative sphere/frustum test used by the Level Editor before submitting an actor's draw calls.
    /// Zero/invalid bounds stay visible so helper actors and partially supported exports are never lost.
    /// </summary>
    public bool IsBoundsVisible(BoxSphereBounds bounds)
    {
        float radius = MathF.Abs(bounds.SphereRadius);
        if (!(radius > 0f) || !float.IsFinite(radius)
            || !float.IsFinite(bounds.Origin.X) || !float.IsFinite(bounds.Origin.Y) || !float.IsFinite(bounds.Origin.Z))
        {
            return true;
        }

        Vector3 viewCenter = Vector3.Transform(bounds.Origin, Camera.ViewMatrix);
        if (viewCenter.Z + radius < Camera.ZNear || viewCenter.Z - radius > Camera.ZFar)
        {
            return false;
        }

        if (Camera.IsOrthographic)
        {
            float halfWidth = Camera.OrthoWidth * 0.5f;
            float halfHeight = Camera.OrthoSize;
            return MathF.Abs(viewCenter.X) <= halfWidth + radius
                   && MathF.Abs(viewCenter.Y) <= halfHeight + radius;
        }

        float tanY = MathF.Tan(Camera.FOV * 0.5f);
        float tanX = tanY * Camera.aspect;
        float depth = MathF.Max(viewCenter.Z, Camera.ZNear);
        // Plane-normal factors make this conservative for spheres intersecting a side plane.
        float xAllowance = radius * MathF.Sqrt(1f + tanX * tanX);
        float yAllowance = radius * MathF.Sqrt(1f + tanY * tanY);
        return MathF.Abs(viewCenter.X) <= depth * tanX + xAllowance
               && MathF.Abs(viewCenter.Y) <= depth * tanY + yAllowance;
    }

    public bool ScreenToPixel(Vector4 point, out Vector2 pixel)
    {
        if (point.W <= 0f)
        {
            pixel = Vector2.Zero;
            return false;
        }

        float invW = 1f / point.W;
        pixel = new Vector2((0.5f + point.X * 0.5f * invW) * Width, (0.5f - point.Y * 0.5f * invW) * Height);
        return true;
    }

    public bool WorldToPixel(Vector3 point, out Vector2 pixel) => ScreenToPixel(WorldToScreen(point), out pixel);

    public Texture2D LoadUnrealTexture(ExportEntry texture2DExport)
    {
        if (texture2DExport.ClassName is "TextureRenderTarget2D" or "TextureMovie")
        {
            return WhiteTex;
        }
        var unrealTexture = new LECTexture2D(texture2DExport);
        return this.LoadUnrealMip(unrealTexture.GetTopMip(), LegendaryExplorerCore.Textures.Image.getPixelFormatType(unrealTexture.Export.GetProperty<EnumProperty>("Format").Value.Name));
    }

    public Texture2D LoadUnrealTextureCube(ExportEntry textureCubeExport, PackageCache packageCache = null)
    {
        if (textureCubeExport.ClassName != "TextureCube") throw new ArgumentException("Expected a TextureCube export.", nameof(textureCubeExport));

        packageCache ??= this.PackageCache;

        var props = textureCubeExport.GetProperties();
        var faceTextures = new Fixed6<LECTexture2D>();
        Span<string> facePropNames = ["FacePosX", "FaceNegX", "FacePosY", "FaceNegY", "FacePosZ", "FaceNegZ"];
        for (int i = 0; i < 6; i++)
        {
            ObjectProperty faceProp = props.GetProp<ObjectProperty>(facePropNames[i]);
            if (faceProp is null)
            {
                return WhiteTextureCube;
            }

            var faceExport = faceProp.ResolveToExport(textureCubeExport.FileRef, packageCache);
            if (faceExport is null)
            {
                var unresolvedEntry = textureCubeExport.FileRef.GetEntry(faceProp.Value);
                Debug.WriteLine($"Unable to resolve texture cube face '{unresolvedEntry?.InstancedFullPath ?? faceProp.Value.ToString()}' for cube '{textureCubeExport.InstancedFullPath}'. Falling back to white texture cube.");
                return WhiteTextureCube;
            }

            faceTextures[i] = new(faceExport);
        }
        var pixelData = new Fixed6<byte[]>();

        //should be the same for all textures
        uint size = (uint)faceTextures[0].GetTopMip().width;
        var format = (Format)LegendaryExplorerCore.Textures.TexConverter.GetDXGIFormatForPixelFormat(
            LegendaryExplorerCore.Textures.Image.getPixelFormatType(faceTextures[0].Export.GetProperty<EnumProperty>("Format").Value.Name));
        for (int i = 0; i < 6; i++)
        {
            pixelData[i] = LECTexture2D.GetTextureData(faceTextures[i].GetTopMip(), textureCubeExport.Game);
        }
        return this.LoadTextureCube(size, format, pixelData);
    }
}

file class BlendDescComparer : IEqualityComparer<RenderTargetBlendDescription>
{
    public bool Equals(RenderTargetBlendDescription x, RenderTargetBlendDescription y)
    {
        return x.IsBlendEnabled.Equals(y.IsBlendEnabled)
               && x.SourceBlend == y.SourceBlend
               && x.DestinationBlend == y.DestinationBlend
               && x.BlendOperation == y.BlendOperation
               && x.SourceAlphaBlend == y.SourceAlphaBlend
               && x.DestinationAlphaBlend == y.DestinationAlphaBlend
               && x.AlphaBlendOperation == y.AlphaBlendOperation
               && x.RenderTargetWriteMask == y.RenderTargetWriteMask;
    }

    public int GetHashCode(RenderTargetBlendDescription obj)
    {
        return HashCode.Combine(obj.IsBlendEnabled, (int)obj.SourceBlend,
            (int)obj.DestinationBlend, (int)obj.BlendOperation,
            (int)obj.SourceAlphaBlend, (int)obj.DestinationAlphaBlend,
            (int)obj.AlphaBlendOperation, (int)obj.RenderTargetWriteMask);
    }
}
