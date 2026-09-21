#version 450
layout(location=0) in vec3 position;
layout(location=1) in vec3 normal;
layout(location=2) in vec2 uv;
layout(location=3) in vec4 color;
layout(location=4) in vec4 weights;
layout(location=5) in vec4 boneIndices;
layout(set=1,binding=0) uniform Pose { mat4 viewProjection; mat4 bones[40]; } pose;
layout(set=1,binding=1) uniform Outline { vec4 parameters; } outline;
layout(location=0) out vec2 textureCoordinate;
layout(location=1) out vec4 vertexColor;
layout(location=2) out float cameraFacing;
void main() {
    mat4 skin = pose.bones[int(boneIndices.x)] * weights.x
              + pose.bones[int(boneIndices.y)] * weights.y
              + pose.bones[int(boneIndices.z)] * weights.z
              + pose.bones[int(boneIndices.w)] * weights.w;
    vec3 worldPosition = (vec4(position, 1.0) * skin).xyz;
    vec3 worldNormal = (vec4(normal, 0.0) * skin).xyz;
    vec4 clip = vec4(worldPosition, 1.0) * pose.viewProjection;
    cameraFacing = dot(normalize(worldNormal), -normalize(pose.viewProjection[3].xyz));
    if (outline.parameters.x > 0.0) {
        vec2 projectedNormal = (vec4(worldNormal, 0.0) * pose.viewProjection).xy;
        if (dot(projectedNormal, projectedNormal) > 1e-12)
            clip.xy += normalize(projectedNormal) * outline.parameters.x * color.g * clip.w;
    }
    gl_Position = clip;
    textureCoordinate = uv;
    vertexColor = color;
}
