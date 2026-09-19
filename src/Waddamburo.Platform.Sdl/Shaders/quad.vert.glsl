#version 450

layout(set = 1, binding = 0) uniform QuadUniforms
{
    vec4 topLeft;
    vec4 topRight;
    vec4 bottomRight;
    vec4 bottomLeft;
    vec4 multiplyColor;
    vec4 addColor;
} quad;

layout(location = 0) out vec2 textureCoordinate;
layout(location = 1) out vec4 colorMultiply;
layout(location = 2) out vec4 colorAdd;

const int cornerIndices[6] = int[](0, 1, 2, 0, 2, 3);

void main()
{
    vec4 corners[4] = vec4[](quad.topLeft, quad.topRight, quad.bottomRight, quad.bottomLeft);
    vec4 vertex = corners[cornerIndices[gl_VertexIndex]];
    vec2 stagePosition = vertex.xy;
    gl_Position = vec4(stagePosition.x * 2.0 - 1.0, 1.0 - stagePosition.y * 2.0, 0.0, 1.0);
    textureCoordinate = vertex.zw;
    colorMultiply = quad.multiplyColor;
    colorAdd = quad.addColor;
}
