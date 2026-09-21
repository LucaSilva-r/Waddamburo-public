#version 450
layout(set=2,binding=0) uniform sampler2D characterTexture;
layout(set=3,binding=0) uniform Post { vec4 parameters; } post;
layout(location=0) in vec2 textureCoordinate;
layout(location=0) out vec4 outputColor;
void main() {
    vec4 center = texture(characterTexture, textureCoordinate);
    float coverage = 0.0;
    for (int index = 0; index < 16; index++) {
        float angle = 6.28318530718 * (float(index) + 0.5) / 16.0;
        vec2 delta = vec2(cos(angle), sin(angle)) * post.parameters.yz * post.parameters.x;
        coverage = max(coverage, texture(characterTexture, textureCoordinate + delta).a);
    }
    outputColor = vec4(center.a > 0.0 ? center.rgb : vec3(0.0), max(coverage, center.a));
}
