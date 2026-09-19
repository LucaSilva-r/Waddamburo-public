#version 450

layout(set = 1, binding = 0) uniform QuadUniforms
{
    vec4 destination;
    vec4 uvRectangle;
    vec4 multiplyColor;
    vec4 addColor;
} quad;

layout(location = 0) out vec2 textureCoordinate;
layout(location = 1) out vec4 colorMultiply;
layout(location = 2) out vec4 colorAdd;

const vec2 corners[6] = vec2[](
    vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(1.0, 1.0),
    vec2(0.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));

void main()
{
    vec2 corner = corners[gl_VertexIndex];
    vec2 stagePosition = quad.destination.xy + corner * quad.destination.zw;
    gl_Position = vec4(stagePosition.x * 2.0 - 1.0, 1.0 - stagePosition.y * 2.0, 0.0, 1.0);
    textureCoordinate = quad.uvRectangle.xy + corner * quad.uvRectangle.zw;
    colorMultiply = quad.multiplyColor;
    colorAdd = quad.addColor;
}
