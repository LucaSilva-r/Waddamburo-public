#version 450
layout(set=2,binding=0) uniform sampler2D materialTexture;
layout(set=3,binding=0) uniform Material { vec4 parameters; vec4 replaceRed; vec4 replaceGreen; vec4 replaceBlue; } material;
layout(location=0) in vec2 textureCoordinate;
layout(location=1) in vec4 vertexColor;
layout(location=2) in float cameraFacing;
layout(location=0) out vec4 outputColor;
void main() {
    vec4 sampled = texture(materialTexture, textureCoordinate);
    int kind = int(material.parameters.y);
    vec4 result = sampled;
    if (kind == 1)
        result = vec4(sampled.r * material.replaceRed.rgb + sampled.g * material.replaceGreen.rgb + sampled.b * material.replaceBlue.rgb, sampled.a);
    else if (kind == 3 || kind == 8) {
        if (cameraFacing > 0.3) discard;
        result = vec4(0.0, 0.0, 0.0, sampled.a);
    }
    if (result.a <= material.parameters.x) discard;
    outputColor = result;
}
