Texture2D SceneTexture : register(t0);
Texture2D BloomTexture : register(t1);
SamplerState PostProcessSampler : register(s0);

cbuffer PostProcessConstants : register(b0)
{
    // x: scale, y: extraction threshold, z: screen-blend threshold,
    // w: postprocess enabled
    float4 BloomParameters;
    // xy: separable blur direction, zw: inverse dimensions of this bloom level
    float4 PassParameters;
};

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

float4 BloomExtractPS(PixelInput input) : SV_TARGET
{
    float3 scene = max(SceneTexture.Sample(PostProcessSampler, input.UV).rgb, 0);
    float luminance = dot(scene, float3(0.2126, 0.7152, 0.0722));
    float contribution = saturate((luminance - BloomParameters.y) / max(luminance, 0.0001));
    return float4(scene * contribution, 1);
}

float4 BloomBlurPS(PixelInput input) : SV_TARGET
{
    float2 texelOffset = PassParameters.xy * PassParameters.zw;
    float3 color = BloomTexture.Sample(PostProcessSampler, input.UV).rgb * 0.227027;
    color += BloomTexture.Sample(PostProcessSampler, input.UV + texelOffset * 1.384615).rgb * 0.316216;
    color += BloomTexture.Sample(PostProcessSampler, input.UV - texelOffset * 1.384615).rgb * 0.316216;
    color += BloomTexture.Sample(PostProcessSampler, input.UV + texelOffset * 3.230769).rgb * 0.070270;
    color += BloomTexture.Sample(PostProcessSampler, input.UV - texelOffset * 3.230769).rgb * 0.070270;
    return float4(color, 1);
}

float3 ApplyFittedSceneGrade(float3 displayScene)
{
    float3 x = max(displayScene, 0);

    // Independent fifth-order fits from the clean EARTH_NONE -> EARTH_NB
    // reference. The stronger blue response is what gives UE3's scene grade
    // its cool cast while rolling white clouds down far enough to retain detail.
    float3 graded;
    graded.r = (((((1.8034979 * x.r - 4.0645962) * x.r + 2.6163797) * x.r
        - 0.2553922) * x.r + 0.6579906) * x.r + 0.0051441);
    graded.g = (((((1.7093550 * x.g - 3.7889508) * x.g + 2.3307268) * x.g
        - 0.1316716) * x.g + 0.6402616) * x.g + 0.0054312);
    graded.b = (((((1.5008618 * x.b - 3.0413863) * x.b + 1.3247533) * x.b
        + 0.4697336) * x.b + 0.5991276) * x.b + 0.0081227);

    return saturate(graded);
}

float4 CompositePS(PixelInput input) : SV_TARGET
{
    float3 scene = max(SceneTexture.Sample(PostProcessSampler, input.UV).rgb, 0);
    float receiverMask = 0;
    float3 bloom = 0;
    if (BloomParameters.w > 0.5)
    {
        float sceneLuminance = dot(scene, float3(0.2126, 0.7152, 0.0722));
        // The 0.5 -> 1.0 reference changes dark-sky bloom by ~1.5x after
        // grading, confirming UE3's unnormalised threshold-minus-luminance mask.
        receiverMask = saturate(BloomParameters.z - sceneLuminance);
        bloom = BloomTexture.Sample(PostProcessSampler, input.UV).rgb;
        bloom = 1.0 - exp(-max(bloom * BloomParameters.x, 0));
    }

    // Grade and compress the base scene first. Bloom is then restored onto that
    // result so only luminous features reclaim the top of the display range.
    float3 display = pow(max(scene, 0), 1.0 / 2.2);
    if (BloomParameters.w > 0.5)
    {
        display = ApplyFittedSceneGrade(display);
        display += bloom * receiverMask;
    }
    return float4(saturate(display), 1);
}
