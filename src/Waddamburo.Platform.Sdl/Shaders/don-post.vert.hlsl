struct Output { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Output main(uint vertexId : SV_VertexID) {
    float2 corner = float2((vertexId << 1) & 2, vertexId & 2);
    Output output;
    output.position = float4(corner * 2 - 1, 0, 1);
    output.uv = float2(corner.x, 1 - corner.y);
    return output;
}
