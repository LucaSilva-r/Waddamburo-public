cbuffer QuadUniforms : register(b0, space1)
{
    float4 topLeft;
    float4 topRight;
    float4 bottomRight;
    float4 bottomLeft;
    float4 multiplyColor;
    float4 addColor;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float2 textureCoordinate : TEXCOORD0;
    float4 colorMultiply : TEXCOORD1;
    float4 colorAdd : TEXCOORD2;
};

VertexOutput main(uint vertexId : SV_VertexID)
{
    static const uint cornerIndices[6] = { 0, 1, 2, 0, 2, 3 };
    float4 corners[4] = { topLeft, topRight, bottomRight, bottomLeft };

    float4 vertex = corners[cornerIndices[vertexId]];
    float2 stagePosition = vertex.xy;
    VertexOutput output;
    output.position = float4(stagePosition.x * 2.0 - 1.0, 1.0 - stagePosition.y * 2.0, 0.0, 1.0);
    output.textureCoordinate = vertex.zw;
    output.colorMultiply = multiplyColor;
    output.colorAdd = addColor;
    return output;
}
