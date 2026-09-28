$input v_texcoord0, v_color0, v_facing

#include <bgfx_shader.sh>

SAMPLER2D(s_material, 0);
// x: alpha-test reference, y: material kind.
uniform vec4 u_material;
uniform vec4 u_replaceRed;
uniform vec4 u_replaceGreen;
uniform vec4 u_replaceBlue;

void main()
{
    vec4 sampled = texture2D(s_material, v_texcoord0);
    int kind = int(u_material.y + 0.5);
    vec4 result = sampled;
    if (kind == 1)
        result = vec4(sampled.r * u_replaceRed.rgb + sampled.g * u_replaceGreen.rgb + sampled.b * u_replaceBlue.rgb, sampled.a);
    else if (kind == 3 || kind == 8)
    {
        if (v_facing > 0.3)
            discard;
        result = vec4(0.0, 0.0, 0.0, sampled.a);
    }
    if (result.a <= u_material.x)
        discard;
    gl_FragColor = result;
}
