#version 450

layout(set = 2, binding = 0) uniform sampler2D sourceTexture;

layout(location = 0) in vec2 textureCoordinate;
layout(location = 1) in vec4 colorMultiply;
layout(location = 2) in vec4 colorAdd;
layout(location = 0) out vec4 outputColor;

void main()
{
    vec4 straightColor = texture(sourceTexture, textureCoordinate) * colorMultiply + colorAdd;
    if (straightColor.a < 0.5) discard;
    outputColor = vec4(straightColor.rgb * straightColor.a, straightColor.a);
}
