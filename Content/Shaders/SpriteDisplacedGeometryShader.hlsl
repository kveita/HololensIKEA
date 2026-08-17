// SpriteDisplacedGeometryShader.hlsl
//
// Pass-through geometry shader for the displaced sprite on non-VPRT devices.
// Promotes TEXCOORD2 (viewId from VS) to SV_RenderTargetArrayIndex and also
// passes TEXCOORD1 (disp) through to the pixel shader.

struct GeometryShaderInput
{
    float4 pos    : SV_POSITION;
    float2 uv     : TEXCOORD0;
    float  disp   : TEXCOORD1;
    uint   viewId : TEXCOORD2;
};

struct GeometryShaderOutput
{
    float4 pos    : SV_POSITION;
    float2 uv     : TEXCOORD0;
    float  disp   : TEXCOORD1;
    uint   viewId : SV_RenderTargetArrayIndex;
};

[maxvertexcount(3)]
void main(triangle GeometryShaderInput input[3],
          inout TriangleStream<GeometryShaderOutput> outStream)
{
    GeometryShaderOutput output;
    [unroll(3)]
    for (int i = 0; i < 3; i++)
    {
        output.pos    = input[i].pos;
        output.uv     = input[i].uv;
        output.disp   = input[i].disp;
        output.viewId = input[i].viewId;
        outStream.Append(output);
    }
}
