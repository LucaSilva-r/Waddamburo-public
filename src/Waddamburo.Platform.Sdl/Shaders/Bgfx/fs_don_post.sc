$input v_texcoord0

#include <bgfx_shader.sh>

SAMPLER2D(s_character, 0);
// x: silhouette width in target pixels, yz: texel size.
uniform vec4 u_post;

void main()
{
    vec4 center = texture2D(s_character, v_texcoord0);
    float coverage = 0.0;
    for (int index = 0; index < 16; index++)
    {
        float angle = 6.28318530718 * (float(index) + 0.5) / 16.0;
        vec2 delta = vec2(cos(angle), sin(angle)) * u_post.yz * u_post.x;
        coverage = max(coverage, texture2D(s_character, v_texcoord0 + delta).a);
    }
    gl_FragColor = vec4(center.a > 0.0 ? center.rgb : vec3(0.0, 0.0, 0.0), max(coverage, center.a));
}
