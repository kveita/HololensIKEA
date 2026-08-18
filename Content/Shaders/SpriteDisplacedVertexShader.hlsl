// SpriteDisplacedVertexShader.hlsl  (VPRT path)
//
// Displaced-mesh vertex shader for the product sprite.
// A 64×64 subdivided quad is used instead of a flat 4-vertex quad.
// Each vertex is displaced in local Z by sampling the displacement map
// (produced by ProductDepthAnalyzer) at the vertex UV, scaled by the
// product's real depth dimension from the JSON.
//
// Uses instanced stereo: SV_InstanceID selects the eye; the VPRT extension
// allows the VS to write SV_RenderTargetArrayIndex directly.

Texture2D<float> displacementTex : register(t1);
SamplerState     pointSampler    : register(s1);

cbuffer ModelConstantBuffer : register(b0)
{
    float4x4 model;
};

cbuffer ViewProjectionConstantBuffer : register(b1)
{
    float4x4 viewProjection[2];
};

cbuffer SpriteConstantBuffer : register(b2)
{
    float whiteThreshold;
    float whiteSoftness;
    float opacity;
    float depthScale;   // product depth in metres; controls Z displacement magnitude
};

struct VertexShaderInput
{
    float3 pos    : POSITION;
    float2 uv     : TEXCOORD0;
    uint   instId : SV_InstanceID;
};

struct VertexShaderOutput
{
    float4 pos    : SV_POSITION;
    float2 uv     : TEXCOORD0;
    float  disp   : TEXCOORD1;   // passed to PS for AO + parallax
    uint   viewId : SV_RenderTargetArrayIndex;
};

VertexShaderOutput main(VertexShaderInput input)
{
    // Sample displacement map: 0 = most recessed, 1 = most protruding.
    float disp = displacementTex.SampleLevel(pointSampler, input.uv, 0);

    // Displace vertex in local +Z (toward camera) by (disp - 0.5) × depthScale.
    // Centring at 0.5 keeps mid-depth pixels at the nominal face plane.
    float3 displaced = input.pos;
    displaced.z += (disp - 0.5f) * depthScale;

    int idx = input.instId % 2;
    float4 worldPos = mul(float4(displaced, 1.0f), model);
    float4 clipPos  = mul(worldPos, viewProjection[idx]);

    VertexShaderOutput output;
    output.pos    = clipPos;
    output.uv     = input.uv;
    output.disp   = disp;
    output.viewId = idx;
    return output;
}
