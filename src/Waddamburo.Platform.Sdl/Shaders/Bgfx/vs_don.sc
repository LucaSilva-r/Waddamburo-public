$input a_position, a_normal, a_texcoord0, a_color0, a_weight, a_indices
$output v_texcoord0, v_color0, v_facing

#include <bgfx_shader.sh>

uniform mat4 u_camera;
uniform mat4 u_bones[40];
// x: hull line width in target pixels (0 = none), yz: clip units per target pixel.
uniform vec4 u_outline;

void main()
{
    mat4 skin = u_bones[int(a_indices.x)] * a_weight.x
              + u_bones[int(a_indices.y)] * a_weight.y
              + u_bones[int(a_indices.z)] * a_weight.z
              + u_bones[int(a_indices.w)] * a_weight.w;
    vec3 worldPosition = mul(skin, vec4(a_position, 1.0)).xyz;
    vec3 worldNormal = mul(skin, vec4(a_normal, 0.0)).xyz;
    vec4 clip = mul(u_camera, vec4(worldPosition, 1.0));
    // The camera's clip-W row: the view direction for the rim test.
    vec3 viewAxis = mul(vec4(0.0, 0.0, 0.0, 1.0), u_camera).xyz;
    v_facing = dot(normalize(worldNormal), -normalize(viewAxis));
    if (u_outline.x > 0.0)
    {
        vec2 projectedNormal = mul(u_camera, vec4(worldNormal, 0.0)).xy;
        // Offset in target pixels so the hull line keeps its width on non-square targets.
        if (dot(projectedNormal, projectedNormal) > 1e-12)
            clip.xy += normalize(projectedNormal / u_outline.yz) * u_outline.x * u_outline.yz * a_color0.g * clip.w;
    }
    gl_Position = clip;
    v_texcoord0 = a_texcoord0;
    v_color0 = a_color0;
}
