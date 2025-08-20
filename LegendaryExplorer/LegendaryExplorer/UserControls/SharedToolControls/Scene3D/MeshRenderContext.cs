using LegendaryExplorer.Misc;
using LegendaryExplorer.Resources;
using LegendaryExplorer.UserControls.ExportLoaderControls.TextureViewer;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using D2D = SharpDX.Direct2D1;
using DW = SharpDX.DirectWrite;
using Texture2D = SharpDX.Direct3D11.Texture2D;

namespace LegendaryExplorer.UserControls.SharedToolControls.Scene3D;

/// <summary>
/// Handles rendering of mesh data
/// </summary>
public class MeshRenderContext : RenderContext
{
    /// <summary>
    /// The current flags for rendering textures. This renderer does not support 'SetAlphaAsBlack' or 'ReconstructZ'
    /// </summary>
    public TextureRenderContext.TextureViewFlags CurrentTextureViewFlags = TextureRenderContext.TextureViewFlags.EnableRedChannel | TextureRenderContext.TextureViewFlags.EnableGreenChannel | TextureRenderContext.TextureViewFlags.EnableBlueChannel | TextureRenderContext.TextureViewFlags.EnableAlphaChannel;

    public struct WorldConstants
    {
        public Matrix4x4 Projection;
        public Matrix4x4 View;
        public Matrix4x4 Model;
        public TextureRenderContext.TextureViewFlags Flags;
        public int Padding1;
        public int Padding2;
        public int Padding3; // Aligns on 16 byte boundary

        public WorldConstants(Matrix4x4 Projection, Matrix4x4 View, Matrix4x4 Model, TextureRenderContext.TextureViewFlags flags)
        {
            this.Projection = Projection;
            this.View = View;
            this.Model = Model;
            this.Flags = flags;
            Padding1 = Padding2 = Padding3 = 0;
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
        E = 0b100000
    }

    public Color BackgroundColor = Color.FromArgb(255,255,255,255); //Default

    #region Size-Dependent Resources
    public RenderTargetView BackbufferView { get; private set; }
    public Texture2D DepthBuffer { get; private set; } // also called Depth-Stencil, but we don't use stencil at the moment.
    public DepthStencilView DepthBufferView { get; private set; }

    private D2D.RenderTarget renderTarget2D;
    private DW.TextFormat statsTextFormat;
    private DW.TextFormat errorTextFormat;
    private D2D.SolidColorBrush statsTextBrush;
    private D2D.SolidColorBrush errorTextBrush;
    #endregion
    public GenericEffect<WorldConstants> FallbackEffect { get; private set; }
    public LEEffect LEEffect { get; private set; }
    private Texture2D DefaultTexture;
    private Texture2D WhiteTextureCube;
    private Texture2D WhiteTex;
    public ShaderResourceView DefaultTextureView { get; private set; }
    public ShaderResourceView WhiteTextureCubeView { get; private set; }
    public ShaderResourceView WhiteTexView { get; private set; }
    private RasterizerState FillRasterizerState;
    private RasterizerState WireframeRasterizerState;
    public SamplerState SampleState { get; private set; }
    public readonly SceneCamera Camera = new();
    private bool wireframe;
    public bool Wireframe
    {
        get => wireframe;
        set
        {
            wireframe = value;
            if (Device != null)
            {
                ImmediateContext.Rasterizer.State = wireframe ? WireframeRasterizerState : FillRasterizerState;
            }
        }
    }
    private KeyStates PressedKeys; 
    private MouseButtons PressedMouseButton;
    public float CameraSpeed { get; set; } = 500.0f; // Units per second
    public float Time { get; private set; }
    public uint NumFrames { get; private set; }

    private float FPS;
    private float lastFPSTime;
    private float lastFPSFrame;
    string ErrorText;

    public event EventHandler<float> UpdateScene;
    public event EventHandler RenderScene;

    private readonly Dictionary<RenderTargetBlendDescription, BlendState> BlendStateCache = new(new BlendDescComparer());
    private readonly Dictionary<Guid, VertexShader> VertexShaderCache = [];
    private readonly Dictionary<Guid, InputLayout> InputLayoutCache = [];
    private readonly Dictionary<Guid, PixelShader> PixelShaderCache = [];
    private readonly Dictionary<string, ModelPreviewMaterial> MaterialCache = [];
    public readonly PreviewTextureCache TextureCache;
    public readonly PackageCache PackageCache;

    public MeshRenderContext()
    {
        this.Camera.FocusDepth = 100.0f;
        TextureCache = new PreviewTextureCache(this);
        PackageCache = new PackageCache();
    }

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

        if (Camera.FirstPerson)
        {
            if (PressedKeys.HasFlag(KeyStates.W))
            {
                Camera.Position += Camera.CameraForward * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.S))
            {
                Camera.Position += -Camera.CameraForward * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.A))
            {
                Camera.Position += Camera.CameraLeft * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.D))
            {
                Camera.Position += -Camera.CameraLeft * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.Q))
            {
                Camera.Position += -Vector3.UnitY * timestep * CameraSpeed;
            }
            if (PressedKeys.HasFlag(KeyStates.E))
            {
                Camera.Position += Vector3.UnitY * timestep * CameraSpeed;
            }
        }

        UpdateScene?.Invoke(null, timestep);
    }

    public override void Render()
    {
        NumFrames++;
        // Clear the color and depth buffers
        if (DepthBufferView != null && BackbufferView != null)
        {
            ImmediateContext.ClearDepthStencilView(DepthBufferView, DepthStencilClearFlags.Depth, 1.0f, 0);
            ImmediateContext.ClearRenderTargetView(BackbufferView, new RawColor4(BackgroundColor.R / 255.0f, BackgroundColor.G / 255.0f, BackgroundColor.B / 255.0f, BackgroundColor.A / 255.0f));

            if (ErrorText is not null)
            {
                renderTarget2D.BeginDraw();
                {
                    var size = renderTarget2D.Size;
                    renderTarget2D.DrawText($"{ErrorText}", errorTextFormat, new RawRectangleF(0, 0, size.Width, size.Height), errorTextBrush);
                }
                renderTarget2D.EndDraw();
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

            if (App.IsDebug)
            {
                //render D2D overlay
                renderTarget2D.BeginDraw();
                {
                    var size = renderTarget2D.Size;
                    renderTarget2D.DrawText($"{FPS} fps\n{Camera.Position}", statsTextFormat, new RawRectangleF(0, 0, size.Width, size.Height), statsTextBrush);
                }
                renderTarget2D.EndDraw();
            }
        }

        base.Render();
    }

    public void UpdateLECameraConstants()
    {
        SceneCamera camera = Camera;
        Matrix4x4 viewMatrix = camera.ViewMatrix;
        var vsConstants = new LEVSConstants
        {
            ViewProjectionMatrix = viewMatrix * camera.ProjectionMatrix,
            CameraPosition = new Vector4(camera.Position, 1),
            PreViewTranslation = Vector4.Zero,
        };
        float depthMul = camera.ProjectionMatrix[2, 2];
        float depthAdd = camera.ProjectionMatrix[3, 2];
        if (false) //TODO: check if Z is inverted, if so this should be true
        {
            depthMul = 1f - depthMul;
            depthAdd = -depthAdd;
        }
        var psConstants = new LEPSConstants
        {
            ScreenPositionScaleBias = new Vector4(1f / 2f, 1f / -2f, (Height / 2f + 0.5f) / Height, (Width / 2f + 0.5f) / Width),
            MinZ_MaxZRatio = new Vector4(depthAdd, depthMul, 1f / depthAdd, depthMul / depthAdd),
            DynamicScale = Vector4.One,
        };

        LEEffect.UpdateCameraConstants(ImmediateContext, ref vsConstants, ref psConstants);
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
        var ssd = new SamplerStateDescription
        {
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            Filter = Filter.Anisotropic,
            MaximumAnisotropy = 8
        };
        SampleState = new SamplerState(Device, ssd);
        //just set all the sample state slots.
        const int numSampleStates = 16;
        for (int i = 0; i < numSampleStates; i++)
        {
            ImmediateContext.PixelShader.SetSampler(i, SampleState);
        }

        // Load the default texture
        DefaultTexture = this.LoadTextureFromFile(Path.Combine(AppDirectories.ExecFolder, "Default.png"));
        DefaultTextureView = new ShaderResourceView(Device, DefaultTexture);

        // Load the default position-texture shader
        FallbackEffect = new GenericEffect<WorldConstants>(Device, EmbeddedResources.StandardShader);

        //create fallback textures
        var whiteCubeData = new Fixed6<byte[]>();
        whiteCubeData[0] = whiteCubeData[1] = whiteCubeData[2] = whiteCubeData[3] = whiteCubeData[4] = whiteCubeData[5] = [255, 255, 255, 255];
        WhiteTextureCube = this.LoadTextureCube(1, Format.R8G8B8A8_UNorm, whiteCubeData);
        WhiteTextureCubeView = new ShaderResourceView(Device, WhiteTextureCube);
        WhiteTex = new Texture2D(Device, new Texture2DDescription{ Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm, SampleDescription = new SampleDescription(1, 0), BindFlags = BindFlags.ShaderResource});
        int white = int.MaxValue;
        Device.ImmediateContext.UpdateSubresource(ref white, WhiteTex, rowPitch: 8);
        WhiteTexView = new ShaderResourceView(Device, WhiteTex);

        LEEffect = new LEEffect(Device);
    }

    public override void CreateSizeDependentResources(int width, int height, Texture2D newBackBuffer)
    {
        base.CreateSizeDependentResources(width, height, newBackBuffer);
        BackbufferView = new RenderTargetView(Device, Backbuffer);
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

        // Set the output-merger pipeline state to write to the created back buffer and depth buffer
        ImmediateContext.OutputMerger.SetRenderTargets(DepthBufferView, BackbufferView);
        ImmediateContext.Rasterizer.SetViewport(0, 0, Width, Height);
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;

        Camera.aspect = (float)Width / Height;


        using var factory = new D2D.Factory(D2D.FactoryType.SingleThreaded, App.IsDebug ? D2D.DebugLevel.Information : D2D.DebugLevel.None);
        renderTarget2D = new D2D.RenderTarget(factory, newBackBuffer.QueryInterface<Surface>(), new D2D.RenderTargetProperties(new D2D.PixelFormat(Format.Unknown, D2D.AlphaMode.Premultiplied)));
        statsTextBrush = new D2D.SolidColorBrush(renderTarget2D, new RawColor4(0, 0, 0, 1), new D2D.BrushProperties { Opacity = 1 });
        errorTextBrush = new D2D.SolidColorBrush(renderTarget2D, new RawColor4(0.2f, 0, 0, 1), new D2D.BrushProperties { Opacity = 1 });
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
    }

    public override void DisposeSizeDependentResources()
    {
        ImmediateContext.OutputMerger.SetRenderTargets((RenderTargetView)null);
        BackbufferView.Dispose();
        DepthBufferView.Dispose();
        DepthBuffer.Dispose();
        renderTarget2D.Dispose();
        statsTextFormat.Dispose();
        errorTextFormat.Dispose();
        statsTextBrush.Dispose();
        errorTextBrush.Dispose();
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
        SampleState?.Dispose();
        FallbackEffect?.Dispose();
        LEEffect?.Dispose();
        FillRasterizerState?.Dispose();
        WireframeRasterizerState?.Dispose();
        EmptyCaches();
        base.DisposeResources();
    }

    public void RenderMeshAsWireframe(MeshElement mesh)
    {
        bool wireframeBackup = Wireframe;
        Wireframe = true;
        var viewConstants = new WorldConstants(Matrix4x4.Transpose(Camera.ProjectionMatrix), Matrix4x4.Transpose(Camera.ViewMatrix), mesh.LocalToWorld, CurrentTextureViewFlags);
        FallbackEffect.PrepDraw(ImmediateContext, AlphaBlendState);
        FallbackEffect.RenderObject(ImmediateContext, viewConstants, mesh, null);
        Wireframe = wireframeBackup;
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
            inputLayout = new InputLayout(Device, shaderBytecode, LEVertex.InputElements);
            InputLayoutCache.Add(id, inputLayout);
        }
        return (shader, inputLayout);
    }

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

    public ModelPreviewMaterial GetCachedMaterial(IEntry matEntry)
    {
        string ifp = matEntry.InstancedFullPath;
        if (MaterialCache.TryGetValue(ifp, out var mat))
        {
            return mat;
        }
        if (matEntry is not ExportEntry matExport)
        {
            matExport = EntryImporter.ResolveImport((ImportEntry)matEntry, PackageCache);
            if (matExport is null)
            {
                Debug.WriteLine("Could not find import material.");
                Debug.WriteLine($"Import material: '{ifp}' from '{matEntry.FileRef.FilePath}'");
                return null;
            }
        }
        mat = new ModelPreviewMaterial(this, matExport);
        MaterialCache.Add(ifp, mat);
        return mat;
    }

    public override void EmptyCaches()
    {
        PackageCache?.ReleasePackages();
        TextureCache?.ExpungeStaleCacheItems();
        MaterialCache.DisposeValuesAndClear();
        BlendStateCache.DisposeValuesAndClear();
        VertexShaderCache.DisposeValuesAndClear();
        InputLayoutCache.DisposeValuesAndClear();
        PixelShaderCache.DisposeValuesAndClear();
    }

    public override bool MouseDown(MouseButtons button, int x, int y)
    {
        if (PressedMouseButton is MouseButtons.None)
        {
            PressedMouseButton = button;
        }
        return false;
    }

    public override bool MouseUp(MouseButtons button, int x, int y)
    {
        bool handled = PressedMouseButton is not MouseButtons.None;

        PressedMouseButton = MouseButtons.None;

        return handled;
    }

    private System.Drawing.Point lastMouse;
    public override bool MouseMove(int x, int y)
    {
        bool handled = false;
        int xDiff = (x - lastMouse.X);
        int yDiff = (y - lastMouse.Y);
        if (Camera.FirstPerson)
        {
            switch (PressedMouseButton)
            {
                case MouseButtons.Left:
                    Debug.WriteLine($"Before {Camera.Position}");
                    var camFwd = (Camera.CameraForward with { Y = 0 }).Normal();
                    Camera.Position += camFwd * -yDiff;
                    Camera.Yaw += xDiff * -0.01f;
                    Debug.WriteLine($"after {Camera.Position}");
                    break;
                case MouseButtons.Middle:
                    break;
                case MouseButtons.Right:
                    Camera.Yaw += xDiff * -0.01f;
                    Camera.Pitch = (Camera.Pitch + yDiff * -0.01f).Clamp(-MathF.PI / 2 + 0.01f, MathF.PI / 2 - 0.01f);
                    break;
            }
        }
        else
        {
            switch (PressedMouseButton)
            {
                //orbiting
                case MouseButtons.Left:
                    Camera.Yaw += xDiff * -0.01f;
                    Camera.Pitch = MathF.Min(MathF.PI / 2, MathF.Max(-MathF.PI / 2, Camera.Pitch + yDiff * -0.01f));
                    handled = true;
                    break;
                //panning
                case MouseButtons.Middle:
                    Camera.Position += Camera.CameraLeft * xDiff * Camera.FocusDepth * 0.004f;
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
        if (Camera.FirstPerson)
        {
            Camera.Position += Camera.CameraForward * (CameraSpeed / 100 ) * delta;
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