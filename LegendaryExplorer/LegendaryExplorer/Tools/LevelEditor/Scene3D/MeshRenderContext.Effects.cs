using System;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Color = System.Windows.Media.Color;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

// fork-vfx: what particle effects and lens flares need from the renderer, so they draw through its passes, caches and bind tracker rather than
// a renderer of their own: a copy of the scene depth for soft particles and depth-biased alpha, a depth state for materials that disable the depth
// test, and a background that the resolve tonemaps (for the VFX preview, which has nothing but particles in front of it)
public partial class MeshRenderContext
{
    private const string EffectsDepthCopyShader = """
        #ifndef MSAA_SAMPLES
        #define MSAA_SAMPLES 1
        #endif
        #if MSAA_SAMPLES > 1
        Texture2DMS<float> SceneDepth : register(t0);
        #else
        Texture2D<float> SceneDepth : register(t0);
        #endif
        //the first sample of the (reversed) device depth, as FSceneRenderTargets' resolved SceneDepthTexture holds it
        float PSCopyDepth(float4 position : SV_POSITION) : SV_Target
        {
            int2 pixel = int2(position.xy);
        #if MSAA_SAMPLES > 1
            return SceneDepth.Load(pixel, 0);
        #else
            return SceneDepth.Load(int3(pixel, 0));
        #endif
        }
        """;

    private PixelShader effectsDepthCopyPixelShader;
    private int effectsDepthCopySampleCount;
    private Texture2D effectsDepthCopy;
    private RenderTargetView effectsDepthCopyTarget;
    private ShaderResourceView effectsDepthCopyView;
    private uint effectsDepthCopyFrame = uint.MaxValue;
    private DepthStencilState effectsNoDepthTestState;

    /// <summary>
    /// This frame's copy of the scene depth (see <see cref="CopySceneDepthForEffects"/>), which materials reading SceneDepth sample. Null if it
    /// wasn't copied this frame
    /// </summary>
    internal ShaderResourceView EffectsSceneDepthView => effectsDepthCopyFrame == NumFrames ? effectsDepthCopyView : null;

    /// <summary>
    /// Copies the depth buffer as it is now into a texture materials can sample (once per frame: later calls keep the first copy), for the
    /// translucent effects drawn after it. The depth buffer is bound as the depth target while drawing, so it can't be sampled directly. Restores
    /// the scene's render targets and default states, and forgets the bind tracker's bindings
    /// </summary>
    internal bool CopySceneDepthForEffects()
    {
        if (EffectsSceneDepthView is not null) return true;
        if (DepthBufferResourceView is null || Width <= 0 || Height <= 0) return false;
        try
        {
            EnsureEffectsDepthCopy();
            DeviceContext ctx = ImmediateContext;
            //the copy may still be bound as a shader resource from the last frame's effects
            ctx.PixelShader.SetShaderResources(0, NoShaderResources);
            ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, effectsDepthCopyTarget);
            ctx.Rasterizer.SetViewport(0, 0, Width, Height);
            ctx.OutputMerger.SetBlendState(null);
            ctx.Rasterizer.State = FillRasterizerState;
            ctx.InputAssembler.InputLayout = null;
            ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            ctx.VertexShader.Set(ResolveVertexShader);
            ctx.PixelShader.Set(effectsDepthCopyPixelShader);
            ctx.PixelShader.SetShaderResource(0, DepthBufferResourceView);
            ctx.Draw(3, 0);
            ctx.PixelShader.SetShaderResource(0, null);
            effectsDepthCopyFrame = NumFrames;
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not copy the scene depth for effects: {e.Message}");
            return false;
        }
        finally
        {
            //rebinds the scene targets (the depth buffer as the depth target again), default states and samplers; forgets the tracker's bindings
            RestoreSceneRenderState();
        }
    }

    private void EnsureEffectsDepthCopy()
    {
        if (effectsDepthCopyPixelShader is null || effectsDepthCopySampleCount != SampleCount)
        {
            effectsDepthCopyPixelShader?.Dispose();
            using ShaderBytecode bytecode = ShaderBytecode.Compile($"#define MSAA_SAMPLES {SampleCount}\n" + EffectsDepthCopyShader, "PSCopyDepth", "ps_5_0").Bytecode;
            effectsDepthCopyPixelShader = new PixelShader(Device, bytecode);
            effectsDepthCopySampleCount = SampleCount;
        }
        if (effectsDepthCopy is not null && effectsDepthCopy.Description.Width == Width && effectsDepthCopy.Description.Height == Height) return;
        DisposeEffectsDepthCopy();
        effectsDepthCopy = new Texture2D(Device, new Texture2DDescription
        {
            ArraySize = 1,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            Format = Format.R32_Float,
            Width = Width,
            Height = Height,
            MipLevels = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default
        });
        effectsDepthCopyTarget = new RenderTargetView(Device, effectsDepthCopy);
        effectsDepthCopyView = new ShaderResourceView(Device, effectsDepthCopy);
    }

    private void DisposeEffectsDepthCopy()
    {
        effectsDepthCopyView?.Dispose();
        effectsDepthCopyTarget?.Dispose();
        effectsDepthCopy?.Dispose();
        effectsDepthCopyView = null;
        effectsDepthCopyTarget = null;
        effectsDepthCopy = null;
        effectsDepthCopyFrame = uint.MaxValue;
    }

    /// <summary>
    /// For effect materials with bDisableDepthTest: neither tested nor written. (Upstream's <see cref="DefaultDepthState"/> and
    /// <see cref="TranslucentDepthState"/> cover the depth-tested cases)
    /// </summary>
    internal DepthStencilState EffectsNoDepthTestState => effectsNoDepthTestState ??= new DepthStencilState(Device, new DepthStencilStateDescription
    {
        IsDepthEnabled = false,
        DepthWriteMask = DepthWriteMask.Zero,
        DepthComparison = Comparison.Always,
        IsStencilEnabled = false
    });

    /// <summary>The depth state an effect draws with: tested (reversed-Z Greater) and written as its material asks</summary>
    internal DepthStencilState GetEffectsDepthState(bool depthTest, bool depthWrite) => depthTest
        ? depthWrite ? DefaultDepthState : TranslucentDepthState
        : EffectsNoDepthTestState;

    /// <summary>Releases what the effects renderers created on this context. Their owners call it as they're disposed</summary>
    internal void DisposeEffectsResources()
    {
        DisposeEffectsDepthCopy();
        effectsDepthCopyPixelShader?.Dispose();
        effectsDepthCopyPixelShader = null;
        effectsNoDepthTestState?.Dispose();
        effectsNoDepthTestState = null;
    }

    /// <summary>
    /// Clears the scene color to <paramref name="background"/> marked as game-shaded (alpha 2, see PSMainResolve), with a color the resolve's
    /// tonemap turns back into <paramref name="background"/>. Everything drawn over it is tonemapped, which a view of effects alone needs:
    /// additive and translucent particles only add to or blend with what's behind them, so they never set the marker themselves
    /// </summary>
    internal void ClearSceneColorAsGameShaded(Color background)
    {
        if (SceneColorView is null) return;
        ImmediateContext.ClearRenderTargetView(SceneColorView, new RawColor4(
            InverseResolveTonemap(background.R / 255f), InverseResolveTonemap(background.G / 255f), InverseResolveTonemap(background.B / 255f), 2f));
    }

    private const float ResolveWhitePoint = 1 / 0.0616082214f;

    //LevelEditorShader.hlsl's FilmicCurve
    private static float ResolveFilmicCurve(float x)
    {
        const float A = 0.6f, B = 0.1f, C = 0.3f, D = 0.2f, E = 0.025f, F = 0.16f;
        return (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F) - E / F;
    }

    /// <summary>The linear scene color PSMainResolve tonemaps and gamma-encodes into <paramref name="displayValue"/></summary>
    private static float InverseResolveTonemap(float displayValue)
    {
        if (displayValue <= 0) return 0;
        if (displayValue >= 1) return ResolveWhitePoint;
        float target = MathF.Pow(displayValue, DisplayGamma) * ResolveFilmicCurve(ResolveWhitePoint);
        float low = 0, high = ResolveWhitePoint;
        //the curve rises monotonically over [0, white point]
        for (int i = 0; i < 32; i++)
        {
            float mid = (low + high) * 0.5f;
            if (ResolveFilmicCurve(mid) < target) low = mid;
            else high = mid;
        }
        return (low + high) * 0.5f;
    }
}
