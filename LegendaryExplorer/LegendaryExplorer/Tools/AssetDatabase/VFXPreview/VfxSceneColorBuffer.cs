using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using Device = SharpDX.Direct3D11.Device;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

/// <summary>
/// Composites every VFX preview draw before converting to the WPF display surface. Cooked particle
/// shaders output HDR emissive color (and may already apply opacity to RGB), so an UNorm target would
/// clip each overlapping particle before the final image can preserve its color.
/// </summary>
internal sealed class VfxSceneColorBuffer : IDisposable
{
    internal const string ResolveShader = """
Texture2D<float4> SceneColor : register(t0);
float3 Filmic(float3 x)
{
    // Hable's filmic response, with the conventional 11.2 white point applied by the resolve.
    // https://filmicworlds.com/blog/filmic-tonemapping-operators/
    return ((x * (0.15 * x + 0.05) + 0.004) / (x * (0.15 * x + 0.5) + 0.06)) - (0.02 / 0.3);
}
float4 VSMain(uint vertexID : SV_VertexID) : SV_POSITION
{
    float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 PSMain(float4 position : SV_POSITION) : SV_TARGET0
{
    float4 scene = SceneColor.Load(int3(position.xy, 0));
    float3 color = max(scene.rgb, 0);
    // Cooked emissive shaders feed a postprocess in game. Use its HDR range here before the
    // display transfer so highlights retain detail and naturally become less saturated.
    float3 mapped = max(Filmic(color) / Filmic(float3(11.2, 11.2, 11.2)), 0);
    mapped = lerp(mapped * 12.92, 1.055 * pow(mapped, 1.0 / 2.4) - 0.055, step(0.0031308, mapped));
    color = mapped;
    return float4(color, saturate(scene.a));
}
""";

    private readonly Device device;
    private readonly VertexShader vertexShader;
    private readonly PixelShader pixelShader;
    private readonly DepthStencilState resolveDepthState;
    private readonly RasterizerState resolveRasterizerState;
    private Texture2D sceneTexture;
    private RenderTargetView sceneTarget;
    private ShaderResourceView sceneView;
    private int width;
    private int height;

    public VfxSceneColorBuffer(Device device)
    {
        this.device = device;
        using var vertexBytecode = ShaderBytecode.Compile(ResolveShader, "VSMain", "vs_5_0");
        using var pixelBytecode = ShaderBytecode.Compile(ResolveShader, "PSMain", "ps_5_0");
        vertexShader = new VertexShader(device, vertexBytecode);
        pixelShader = new PixelShader(device, pixelBytecode);
        resolveDepthState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.Always,
            IsStencilEnabled = false
        });
        resolveRasterizerState = new RasterizerState(device, new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = true
        });
    }

    public void Resize(int newWidth, int newHeight)
    {
        DisposeSizeDependentResources();
        width = newWidth;
        height = newHeight;
        sceneTexture = new Texture2D(device, new Texture2DDescription
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
        sceneTarget = new RenderTargetView(device, sceneTexture);
        sceneView = new ShaderResourceView(device, sceneTexture);
    }

    public void Begin(DeviceContext context, DepthStencilView depthTarget, RenderTargetView hitTarget, RawColor4 background)
    {
        // Background choices are display colors. Invert the resolve for the clear color so changing
        // the HDR pipeline does not change the selected canvas color.
        context.ClearRenderTargetView(sceneTarget, new RawColor4(
            InverseDisplayTransfer(background.R), InverseDisplayTransfer(background.G),
            InverseDisplayTransfer(background.B), background.A));
        context.OutputMerger.SetRenderTargets(depthTarget, sceneTarget, hitTarget);
    }

    private static float InverseDisplayTransfer(float displayValue)
    {
        if (displayValue <= 0) return 0;
        if (displayValue >= 1) return 11.2f;
        float linearValue = displayValue <= 0.04045f
            ? displayValue / 12.92f
            : MathF.Pow((displayValue + 0.055f) / 1.055f, 2.4f);
        static float Filmic(float value) =>
            (value * (0.15f * value + 0.05f) + 0.004f)
            / (value * (0.15f * value + 0.5f) + 0.06f) - 0.02f / 0.3f;
        float desiredFilmicValue = linearValue * Filmic(11.2f);
        float minimum = 0;
        float maximum = 11.2f;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            float midpoint = (minimum + maximum) * 0.5f;
            if (Filmic(midpoint) < desiredFilmicValue) minimum = midpoint;
            else maximum = midpoint;
        }
        return (minimum + maximum) * 0.5f;
    }

    public void Resolve(DeviceContext context, RenderTargetView displayTarget, DepthStencilView depthTarget, RenderTargetView hitTarget)
    {
        using RasterizerState previousRasterizer = context.Rasterizer.State;
        var previousViewports = context.Rasterizer.GetViewports<RawViewportF>();
        using DepthStencilState previousDepth = context.OutputMerger.GetDepthStencilState(out int stencilReference);
        using BlendState previousBlend = context.OutputMerger.GetBlendState(out RawColor4 blendFactor, out int sampleMask);
        using VertexShader previousVertexShader = context.VertexShader.Get();
        using PixelShader previousPixelShader = context.PixelShader.Get();
        using ShaderResourceView previousTexture = context.PixelShader.GetShaderResources(0, 1)[0];
        using InputLayout previousInputLayout = context.InputAssembler.InputLayout;
        PrimitiveTopology previousTopology = context.InputAssembler.PrimitiveTopology;
        try
        {
            context.OutputMerger.SetRenderTargets((DepthStencilView)null, displayTarget);
            context.OutputMerger.SetBlendState(null);
            context.OutputMerger.SetDepthStencilState(resolveDepthState);
            context.Rasterizer.State = resolveRasterizerState;
            context.Rasterizer.SetViewport(0, 0, width, height);
            context.InputAssembler.InputLayout = null;
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.VertexShader.Set(vertexShader);
            context.PixelShader.Set(pixelShader);
            context.PixelShader.SetShaderResource(0, sceneView);
            context.Draw(3, 0);
        }
        finally
        {
            context.PixelShader.SetShaderResource(0, null);
            context.OutputMerger.SetRenderTargets(depthTarget, displayTarget, hitTarget);
            context.OutputMerger.SetDepthStencilState(previousDepth, stencilReference);
            context.OutputMerger.SetBlendState(previousBlend, blendFactor, sampleMask);
            context.Rasterizer.State = previousRasterizer;
            context.Rasterizer.SetViewports(previousViewports);
            context.InputAssembler.InputLayout = previousInputLayout;
            context.InputAssembler.PrimitiveTopology = previousTopology;
            context.VertexShader.Set(previousVertexShader);
            context.PixelShader.Set(previousPixelShader);
            context.PixelShader.SetShaderResource(0, previousTexture);
        }
    }

    public void DisposeSizeDependentResources()
    {
        sceneView?.Dispose();
        sceneTarget?.Dispose();
        sceneTexture?.Dispose();
        sceneView = null;
        sceneTarget = null;
        sceneTexture = null;
    }

    public void Dispose()
    {
        DisposeSizeDependentResources();
        resolveRasterizerState.Dispose();
        resolveDepthState.Dispose();
        pixelShader.Dispose();
        vertexShader.Dispose();
    }
}
