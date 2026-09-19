cbuffer QuadUniforms : register(b0, space1)
{
    float4 destination;
    float4 uvRectangle;
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
    static const float2 corners[6] = {
        float2(0.0, 0.0), float2(1.0, 0.0), float2(1.0, 1.0),
        float2(0.0, 0.0), float2(1.0, 1.0), float2(0.0, 1.0)
    };

    float2 corner = corners[vertexId];
    float2 stagePosition = destination.xy + corner * destination.zw;
    VertexOutput output;
    output.position = float4(stagePosition.x * 2.0 - 1.0, 1.0 - stagePosition.y * 2.0, 0.0, 1.0);
    output.textureCoordinate = uvRectangle.xy + corner * uvRectangle.zw;
    output.colorMultiply = multiplyColor;
    output.colorAdd = addColor;
    return output;
}
