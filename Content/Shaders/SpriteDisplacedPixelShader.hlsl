// SpriteDisplacedPixelShader.hlsl
//
// White-background removal + parallax UV shift + ambient occlusion from
// the displacement map. Used when ProductDepthAnalyzer has run and a
// displacement texture is available.
//
// Two additions over SpritePixelShader:
//
//   PARALLAX OFFSET
//     Recessed pixels (disp < 0.5) shift their UV sampling slightly
//     toward the texture centre, simulating the perspective shift of
//     looking into a deep surface.  Max 4% UV shift.  Cheap; no ray-march.
//
//   AMBIENT OCCLUSION
//     Recessed pixels are darkened (0.72 at deepest, 1.0 at surface peak),
//     reinforcing depth without any extra lighting passes.

Texture2D    productTex    : register(t0);
SamplerState linearSampler : register(s0);

cbuffer SpriteConstantBuffer : register(b2)
{
    float whiteThreshold;
    float whiteSoftness;
    float opacity;
    float depthScale;   // unused in PS; present for CB alignment
    float4 contentBounds;  // (minU, minV, maxU, maxV) of non-white content; stretch to fill quad
};

struct PixelShaderInput
{
    float4 pos  : SV_POSITION;
    float2 uv   : TEXCOORD0;
    float  disp : TEXCOORD1;
};

float4 main(PixelShaderInput input) : SV_TARGET
{
    // --- Remap UV to stretch non-white content to fill the quad ---
    float2 contentUV = contentBounds.xy + input.uv * (contentBounds.zw - contentBounds.xy);

    // --- Cheap parallax offset ---
    float2 centre    = float2(0.5f, 0.5f);
    float  parallax  = (input.disp - 0.5f) * 0.04f;   // max ±2% per component
    float2 uvShifted = saturate(contentUV + (contentUV - centre) * parallax);

    float4 col = productTex.Sample(linearSampler, uvShifted);

    // --- Un-premultiply ---
    float srcAlpha = col.a;
    float3 straight = (srcAlpha > 0.001f)
        ? col.rgb / srcAlpha
        : float3(1.f, 1.f, 1.f);

    // --- Distance from white ---
    float3 distVec = float3(1.f, 1.f, 1.f) - straight;
    float  distRGB = length(distVec * float3(0.30f, 0.59f, 0.11f) * 5.f);

    float keyAlpha = smoothstep(whiteThreshold, whiteThreshold + whiteSoftness, distRGB);

    // --- Spill suppression ---
    float spillFactor = 1.f - saturate(
        (distRGB - whiteThreshold) / max(whiteSoftness, 0.001f));
    float luma = dot(straight, float3(0.2126f, 0.7152f, 0.0722f));
    straight = lerp(straight, float3(luma, luma, luma), spillFactor * 0.5f);

    // --- Ambient occlusion from displacement ---
    // 0.72 at deepest (disp=0), 1.0 at peak (disp=1).
    float ao = lerp(0.72f, 1.0f, input.disp);

    // --- Re-premultiply ---
    float finalAlpha = keyAlpha * srcAlpha * opacity;
    float3 finalRGB  = straight * ao * finalAlpha;

    return float4(finalRGB, finalAlpha);
}
