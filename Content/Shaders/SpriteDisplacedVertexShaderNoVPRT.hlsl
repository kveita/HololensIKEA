// SpriteDisplacedVertexShaderNoVPRT.hlsl  (non-VPRT fallback)
//
// Same as SpriteDisplacedVertexShader but outputs viewId as TEXCOORD2 instead
// of SV_RenderTargetArrayIndex. The SpriteDisplacedGeometryShader pass-through
// then promotes TEXCOORD2 to SV_RenderTargetArrayIndex for non-VPRT devices.

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
    float depthScale;
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
    float  disp   : TEXCOORD1;
    uint   viewId : TEXCOORD2;   // GS will promote to SV_RenderTargetArrayIndex
};

VertexShaderOutput main(VertexShaderInput input)
{
    float disp = displacementTex.SampleLevel(pointSampler, input.uv, 0);

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
