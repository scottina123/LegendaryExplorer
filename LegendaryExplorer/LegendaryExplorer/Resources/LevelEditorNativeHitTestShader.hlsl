struct VS_IN
{
    float4 pos : POSITION0;
};

struct VS_OUT
{
    float4 pos : SV_POSITION;
};

struct PS_OUT
{
    float4 color : SV_TARGET0;
    float4 hitTestID : SV_TARGET1;
};

cbuffer constants
{
    float4x4 projection;
    float4x4 view;
    float4x4 model;
    float3 HitTestID;
    int Flags;
};

#define FLAG_SELECTED (1 << 30)

VS_OUT VSMain(VS_IN input)
{
    VS_OUT result = (VS_OUT)0;
    float4 worldPos = mul(input.pos, model);
    result.pos = mul(mul(worldPos, view), projection);
    return result;
}

PS_OUT PSMain(VS_OUT input)
{
    PS_OUT result = (PS_OUT)0;
    result.color = (Flags & FLAG_SELECTED) == FLAG_SELECTED
        ? float4(0.08f, 0.22f, 1.0f, 0.28f)
        : float4(0, 0, 0, 0);
    result.hitTestID = float4(HitTestID, 1.0f);
    return result;
}
