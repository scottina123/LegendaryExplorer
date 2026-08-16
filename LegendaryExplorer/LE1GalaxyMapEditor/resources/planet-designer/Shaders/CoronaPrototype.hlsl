cbuffer SceneConstants : register(b0)
{
    row_major float4x4 WorldViewProjection;
    row_major float4x4 World;
    float4 CameraPosition;
    float4 RenderOptions;
};

Texture2D CoronaGradient : register(t0);
SamplerState SurfaceSampler : register(s0);

cbuffer CoronaMaterialConstants : register(b1)
{
    float4 CoronaColor;
    // x: Fringe_Bloom, y: Opacity
    float4 CoronaScalars;
};

#define FringeBloom CoronaScalars.x
#define Opacity CoronaScalars.y

struct VertexInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float4 Tangent : TANGENT;
    float2 UV : TEXCOORD0;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
};

PixelInput VSMain(VertexInput input)
{
    PixelInput output;
    output.Position = mul(float4(input.Position, 1), WorldViewProjection);
    output.UV = input.UV;
    return output;
}

float4 PSMain(PixelInput input) : SV_TARGET
{
    float gradient = CoronaGradient.Sample(SurfaceSampler, input.UV).r;
    float fringe = FringeBloom * floor(gradient + 0.35);
    // Stay linear/HDR. The corona is accumulated into the same scene target as
    // the planet and stars before the single full-screen postprocess pass.
    float3 linearCorona = max((gradient * CoronaColor + fringe) * Opacity, 0);
    return float4(linearCorona * 0.64, 0);
}
