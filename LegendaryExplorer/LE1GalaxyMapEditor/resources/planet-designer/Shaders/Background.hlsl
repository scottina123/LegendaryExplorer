Texture2D BackgroundTexture : register(t0);
SamplerState BackgroundSampler : register(s0);

struct PixelInput
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
};

PixelInput VSMain(uint vertexId : SV_VertexID)
{
    PixelInput output;
    output.UV = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(
        output.UV * float2(2, -2) + float2(-1, 1),
        0,
        1);
    return output;
}

float4 PSMain(PixelInput input) : SV_TARGET
{
    return float4(BackgroundTexture.Sample(BackgroundSampler, input.UV).rgb, 1);
}
