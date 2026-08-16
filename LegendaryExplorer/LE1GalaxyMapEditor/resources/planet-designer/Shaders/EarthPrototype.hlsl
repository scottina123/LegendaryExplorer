cbuffer SceneConstants : register(b0)
{
    row_major float4x4 WorldViewProjection;
    row_major float4x4 World;
    float4 CameraPosition;
    // x: ambient SkyLight, z: animation time, w: point lights
    float4 RenderOptions;
};

Texture2D NormalMap : register(t0);
Texture2D CityEmissive : register(t1);
Texture2D ContinentMask01 : register(t2);
Texture2D ContinentMask02 : register(t3);
Texture2D ContinentTexture : register(t4);
Texture2D OceanTexture : register(t5);
Texture2D AtmosphereMaster : register(t6);
SamplerState SurfaceSampler : register(s0);

cbuffer MaterialConstants : register(b1)
{
    float4 CityEmissiveMixer;
    float4 CityEmissiveColor;
    float4 LandmassMixer;
    float4 ContinentMaskMixer;
    float4 ContinentMaskMixer02;
    float4 ContinentColor;
    float4 ContinentColorAlt;
    float4 ContinentTextureMixer;
    float4 BeachColor;
    float4 OceanColor;
    float4 OceanColorAlt;
    float4 OceanTextureMixer;
    float4 SiltColor;
    float4 AtmosphereMixer;
    float4 AtmosphereColor;
    float4 HorizonAtmosphereColor;
    float4 SkyLightColor;
    float4 PointLight1PositionRadius;
    float4 PointLight2PositionRadius;
    float4 PointLight1Color;
    float4 PointLight2Color;
    // x: normal tile, y: bump, z: twinkle, w: city tile
    float4 MaterialScalars0;
    // x: atmosphere U, y: atmosphere V, z: pan, w: minimum
    float4 MaterialScalars1;
    // x: horizon intensity, y: horizon falloff, z: SkyLight brightness
    float4 MaterialScalars2;
    // x/y: point-light brightness, z/w: point-light falloff exponent
    float4 MaterialScalars3;
};

#define NormalMapTile MaterialScalars0.x
#define BumpAmount MaterialScalars0.y
#define EmissiveTwinkleMultiplier MaterialScalars0.z
#define CityEmissiveTile MaterialScalars0.w
#define AtmosphereTileU MaterialScalars1.x
#define AtmosphereTileV MaterialScalars1.y
#define AtmospherePanMultiplier MaterialScalars1.z
#define AtmosphereMin MaterialScalars1.w
#define HorizonAtmosphereIntensity MaterialScalars2.x
#define HorizonAtmosphereFalloff MaterialScalars2.y
#define SkyLightBrightness MaterialScalars2.z
#define PointLight1Brightness MaterialScalars3.x
#define PointLight2Brightness MaterialScalars3.y
#define PointLight1FalloffExponent MaterialScalars3.z
#define PointLight2FalloffExponent MaterialScalars3.w

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
    float3 WorldPosition : TEXCOORD0;
    float3 Normal : TEXCOORD1;
    float4 Tangent : TEXCOORD2;
    float2 UV : TEXCOORD3;
};

PixelInput VSMain(VertexInput input)
{
    PixelInput output;
    output.Position = mul(float4(input.Position, 1), WorldViewProjection);
    output.WorldPosition = mul(float4(input.Position, 1), World).xyz;
    output.Normal = normalize(mul(float4(input.Normal, 0), World).xyz);
    output.Tangent = float4(
        normalize(mul(float4(input.Tangent.xyz, 0), World).xyz),
        input.Tangent.w);
    output.UV = input.UV;
    return output;
}

float PositiveRamp(float numerator, float denominator)
{
    return denominator > 0 ? saturate(numerator / denominator) : 0;
}

float3 SrgbToLinear(float3 color)
{
    float3 low = color / 12.92;
    float3 high = pow((color + 0.055) / 1.055, 2.4);
    return lerp(high, low, step(color, 0.04045));
}

float3 CalculateSurface(float2 uv, out float coastalCityMask)
{
    float2 maskUV = uv * 10;
    float3 mask1 = ContinentMask01.Sample(SurfaceSampler, maskUV).rgb;
    float3 mask2 = ContinentMask02.Sample(SurfaceSampler, maskUV).rgb;
    float rawLand = dot(mask1, ContinentMaskMixer) + dot(mask2, ContinentMaskMixer02);
    float landMask = step(LandmassMixer.y, rawLand);

    float upperBlend = landMask * PositiveRamp(
        LandmassMixer.y - rawLand + LandmassMixer.x,
        LandmassMixer.x);
    coastalCityMask = upperBlend;
    float lowerBlend = (1 - landMask) * PositiveRamp(
        rawLand - LandmassMixer.y + LandmassMixer.z,
        LandmassMixer.z);

    float continentTexture = saturate(dot(
        ContinentTexture.Sample(SurfaceSampler, uv * 15).rgb,
        ContinentTextureMixer));
    float oceanTexture = dot(
        OceanTexture.Sample(SurfaceSampler, uv * 22).rgb,
        OceanTextureMixer);

    float3 land = lerp(ContinentColor, ContinentColorAlt, continentTexture);
    land = lerp(land, BeachColor, upperBlend);

    float3 ocean = lerp(OceanColor, OceanColorAlt, oceanTexture);
    ocean = lerp(ocean, SiltColor, lowerBlend);
    return lerp(ocean, land, landMask);
}

float3 ApplyAtmosphere(float3 surface, PixelInput input)
{
    float3 normal = normalize(input.Normal);
    float3 tangent = normalize(input.Tangent.xyz);
    // Legendary Explorer's glTF tangent points along +U, while +V is
    // cross(tangent, normal) for this mesh (the opposite of glTF's usual
    // cross(normal, tangent) convention).
    float3 bitangent = normalize(cross(tangent, normal)) * input.Tangent.w;
    float3 viewDirection = normalize(CameraPosition.xyz - input.WorldPosition);
    float2 viewOffset = float2(dot(viewDirection, tangent), dot(viewDirection, bitangent)) * -0.026;

    float atmosphereSpeed = RenderOptions.z * (AtmospherePanMultiplier - 1);
    float2 atmospherePan = frac(atmosphereSpeed * float2(0.004, 0.016));
    float2 atmosphereUV =
        input.UV * float2(AtmosphereTileU, AtmosphereTileV) * 30 + atmospherePan;
    float3 directSample = AtmosphereMaster.Sample(SurfaceSampler, atmosphereUV).rgb;
    float3 offsetSample = AtmosphereMaster.Sample(SurfaceSampler, atmosphereUV + viewOffset).rgb;
    float directMask = max(dot(directSample, AtmosphereMixer), AtmosphereMin);
    float edgeMask = pow(max(abs(1 - dot(offsetSample, AtmosphereMixer)), 0.0001), 10);

    return surface * edgeMask + directMask * (AtmosphereColor - surface * edgeMask);
}

float3 CalculateCityEmission(float2 uv, float coastalCityMask)
{
    float cityA = dot(CityEmissive.Sample(SurfaceSampler, uv * 9).rgb, CityEmissiveMixer);
    float cityB = dot(CityEmissive.Sample(SurfaceSampler, uv * CityEmissiveTile * 50).rgb, CityEmissiveMixer);
    return (cityA * EmissiveTwinkleMultiplier + cityB) * CityEmissiveColor * coastalCityMask;
}

float3 CalculateMaterialNormal(PixelInput input)
{
    float2 tangentXY = NormalMap.Sample(SurfaceSampler, input.UV * NormalMapTile * 60).rg * 2 - 1;
    float tangentZ = sqrt(saturate(1 - dot(tangentXY, tangentXY)));
    float3 tangentNormal = normalize(lerp(
        float3(0, 0, 1),
        float3(tangentXY, tangentZ),
        BumpAmount));

    float3 geometryNormal = normalize(input.Normal);
    float3 tangent = normalize(input.Tangent.xyz);
    float3 bitangent = normalize(cross(tangent, geometryNormal)) * input.Tangent.w;
    return normalize(
        tangent * tangentNormal.x +
        bitangent * tangentNormal.y +
        geometryNormal * tangentNormal.z);
}

float3 ApplySkyLight(float3 materialDiffuse, PixelInput input)
{
    float3 geometryNormal = normalize(input.Normal);
    float3 materialNormal = CalculateMaterialNormal(input);
    float3 viewDirection = normalize(CameraPosition.xyz - input.WorldPosition);

    float horizonFresnel = pow(
        max(1 - saturate(dot(geometryNormal, viewDirection)), 0.0001),
        8) * HorizonAtmosphereFalloff;
    float3 horizon = horizonFresnel * HorizonAtmosphereColor * HorizonAtmosphereIntensity;

    // UE3's NoLightMapPolicySkyLight base pass uses the material normal's
    // world-up component to blend two hemispheres. LE1's (X,Y,Z) -> glTF
    // conversion makes UE Z our world Y. The weights and horizon folding below
    // are recovered directly from the paired SkyLight/NoSkyLight DXBC shaders.
    float upperWeight = pow(saturate(materialNormal.y * 0.5 + 0.5), 2);
    float lowerWeight = pow(saturate(materialNormal.y * -0.5 + 0.5), 2);
    float3 upperResponse = upperWeight + horizon * (horizon - upperWeight);
    float3 lowerResponse = lowerWeight + horizon * (horizon - lowerWeight);

    float3 upperSkyColor = SrgbToLinear(SkyLightColor.rgb / 255.0) * SkyLightBrightness;
    float3 lowerSkyColor = 0;
    return materialDiffuse * (
        upperResponse * upperSkyColor +
        lowerResponse * lowerSkyColor);
}

float3 ApplyPointLight(
    float3 materialDiffuse,
    PixelInput input,
    float4 positionRadius,
    float3 byteColor,
    float brightness,
    float falloffExponent)
{
    float3 toLight = positionRadius.xyz - input.WorldPosition;
    float distanceSquared = dot(toLight, toLight);
    float radiusSquared = max(positionRadius.w * positionRadius.w, 0.0001);
    if (distanceSquared >= radiusSquared || brightness <= 0)
    {
        return 0;
    }

    // This is the radial term from FPointLightPolicy: the squared normalized
    // distance is removed from one, then raised to the component falloff.
    float attenuation = pow(
        max(1 - distanceSquared / radiusSquared, 0.0001),
        falloffExponent);
    float3 lightDirection = normalize(toLight);
    float3 geometryNormal = normalize(input.Normal);
    float3 materialNormal = CalculateMaterialNormal(input);
    float3 viewDirection = normalize(CameraPosition.xyz - input.WorldPosition);

    float ndotl = saturate(dot(materialNormal, lightDirection));
    float reflectedDotLight = saturate(dot(
        reflect(-viewDirection, materialNormal),
        lightDirection));
    float specular = 0.1 * pow(max(reflectedDotLight, 0.0001), 5);

    float horizonFresnel = pow(
        max(1 - saturate(dot(geometryNormal, viewDirection)), 0.0001),
        8) * HorizonAtmosphereFalloff;
    float3 horizon = horizonFresnel * HorizonAtmosphereColor * HorizonAtmosphereIntensity;
    float3 diffuseResponse = horizon * (horizon - ndotl) + ndotl;

    float3 lightColor = SrgbToLinear(byteColor / 255.0) * brightness;
    return (materialDiffuse * diffuseResponse + specular) * attenuation * lightColor;
}

float4 PSMain(PixelInput input) : SV_TARGET
{
    float coastalCityMask;
    float3 surface = CalculateSurface(input.UV, coastalCityMask);
    float3 materialDiffuse = ApplyAtmosphere(surface, input);
    float3 materialEmission = CalculateCityEmission(input.UV, coastalCityMask);

    float3 previewColor;
    if (RenderOptions.x > 0.5)
    {
        previewColor = ApplySkyLight(materialDiffuse, input) + materialEmission;
    }
    else if (RenderOptions.w > 0.5)
    {
        // With the base-pass SkyLight disabled, UE3 contributes emission here;
        // the point lights are accumulated additively below.
        previewColor = materialEmission;
    }
    else
    {
        previewColor = materialDiffuse + materialEmission;
    }


    if (RenderOptions.w > 0.5)
    {
        // In UE3 these are separate additive dynamic-light passes. Combining
        // them here is equivalent while keeping this preview to one draw call.
        previewColor += ApplyPointLight(
            materialDiffuse,
            input,
            PointLight1PositionRadius,
            PointLight1Color.rgb,
            PointLight1Brightness,
            PointLight1FalloffExponent);
        previewColor += ApplyPointLight(
            materialDiffuse,
            input,
            PointLight2PositionRadius,
            PointLight2Color.rgb,
            PointLight2Brightness,
            PointLight2FalloffExponent);
    }

    // Keep the material output linear and HDR. Display encoding, colour grading,
    // and bloom belong to the one full-screen scene postprocess pass.
    return float4(max(previewColor, 0), 1);
}
