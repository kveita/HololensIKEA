// Pixel shader for textured quads (keyboard label overlay).
// Samples a texture and uses its alpha for blending.

Texture2D    shaderTexture : register(t0);
SamplerState samplerState  : register(s0);

struct PixelShaderInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

float4 main(PixelShaderInput input) : SV_TARGET
{
    return shaderTexture.Sample(samplerState, input.uv);
}
