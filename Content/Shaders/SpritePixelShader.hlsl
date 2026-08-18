// SpritePixelShader.hlsl
//
// White-background removal pixel shader for product sprite overlay.
// Used by ProductSpriteRenderer when no displacement map is available.
// Works with TextureVertexShader / TextureVertexShaderNoVPRT (reused).
//
// Algorithm:
//   1. Un-premultiply the BGRA8 premultiplied pixel from BitmapDecoder.
//   2. Compute perceptually-weighted Euclidean distance from pure white.
//   3. Map distance → alpha via smoothstep (soft anti-aliased edge).
//   4. Suppress grey fringe (spill) by desaturating near-white pixels.
//   5. Re-premultiply for correct alpha blending.

Texture2D    productTex    : register(t0);
SamplerState linearSampler : register(s0);

cbuffer SpriteConstantBuffer : register(b2)
{
    float whiteThreshold;  // pixels closer to white than this become transparent (default 0.08)
    float whiteSoftness;   // width of alpha ramp (default 0.12)
    float opacity;         // overall opacity multiplier (default 1.0)
    float depthScale;      // unused in basic PS; present for CB size alignment
    float4 contentBounds;  // (minU, minV, maxU, maxV) of non-white content; stretch to fill quad
};

struct PixelShaderInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

float4 main(PixelShaderInput input) : SV_TARGET
{
    // --- Remap UV to stretch non-white content to fill the quad ---
    float2 remappedUV = contentBounds.xy + input.uv * (contentBounds.zw - contentBounds.xy);

    float4 col = productTex.Sample(linearSampler, remappedUV);

    // --- Un-premultiply ---
    // BitmapDecoder uploads as premultiplied BGRA8; we need straight for keying.
    float srcAlpha = col.a;
    float3 straight = (srcAlpha > 0.001f)
        ? col.rgb / srcAlpha
        : float3(1.f, 1.f, 1.f);   // fully-transparent pixel → treat as white

    // --- Distance from white (perceptual channel weights) ---
    // R:0.30 G:0.59 B:0.11 weighted, scaled ×5 to keep threshold values intuitive.
    float3 distVec = float3(1.f, 1.f, 1.f) - straight;
    float  distRGB = length(distVec * float3(0.30f, 0.59f, 0.11f) * 5.f);

    // --- Smooth alpha ramp ---
    // Below threshold   → fully transparent
    // Above threshold+softness → fully opaque
    float keyAlpha = smoothstep(whiteThreshold, whiteThreshold + whiteSoftness, distRGB);

    // --- Spill suppression ---
    // Near-white fringe pixels are desaturated to remove the grey halo.
    float spillFactor = 1.f - saturate(
        (distRGB - whiteThreshold) / max(whiteSoftness, 0.001f));
    float luma = dot(straight, float3(0.2126f, 0.7152f, 0.0722f));
    straight = lerp(straight, float3(luma, luma, luma), spillFactor * 0.5f);

    // --- Re-premultiply with keyed alpha ---
    float finalAlpha = keyAlpha * srcAlpha * opacity;
    float3 finalRGB  = straight * finalAlpha;

    return float4(finalRGB, finalAlpha);
}
