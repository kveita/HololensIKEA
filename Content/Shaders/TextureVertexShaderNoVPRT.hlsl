// Non-VPRT vertex shader for textured quads.
// The geometry shader will set SV_RenderTargetArrayIndex.

cbuffer ModelConstantBuffer : register(b0)
{
    float4x4 model;
};

cbuffer ViewProjectionConstantBuffer : register(b1)
{
    float4x4 viewProjection[2];
};

struct VertexShaderInput
{
    float3 pos : POSITION;
    float2 uv  : TEXCOORD0;
    uint   instId : SV_InstanceID;
};

struct VertexShaderOutput
{
    float4 pos    : SV_POSITION;
    float2 uv     : TEXCOORD0;
    uint   viewId : TEXCOORD1;
};

VertexShaderOutput main(VertexShaderInput input)
{
    VertexShaderOutput output;
    float4 pos = float4(input.pos, 1.0f);
    int idx = input.instId % 2;
    pos = mul(pos, model);
    pos = mul(pos, viewProjection[idx]);
    output.pos    = pos;
    output.uv     = input.uv;
    output.viewId = idx;
    return output;
}
