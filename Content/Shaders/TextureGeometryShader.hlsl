// Pass-through geometry shader for textured quads (non-VPRT devices).

struct GeometryShaderInput
{
    float4 pos    : SV_POSITION;
    float2 uv     : TEXCOORD0;
    uint   viewId : TEXCOORD1;
};

struct GeometryShaderOutput
{
    float4 pos    : SV_POSITION;
    float2 uv     : TEXCOORD0;
    uint   viewId : SV_RenderTargetArrayIndex;
};

[maxvertexcount(3)]
void main(triangle GeometryShaderInput input[3], inout TriangleStream<GeometryShaderOutput> outStream)
{
    GeometryShaderOutput output;
    [unroll(3)]
    for (int i = 0; i < 3; i++)
    {
        output.pos    = input[i].pos;
        output.uv     = input[i].uv;
        output.viewId = input[i].viewId;
        outStream.Append(output);
    }
}
