$input v_texcoord0, v_color0, v_color1

#include <bgfx_shader.sh>

SAMPLER2D(s_texture, 0);

// Straight-alpha colour transform in, premultiplied alpha out.
void main()
{
    vec4 straightColor = clamp(texture2D(s_texture, v_texcoord0) * v_color0 + v_color1, 0.0, 1.0);
    gl_FragColor = vec4(straightColor.rgb * straightColor.a, straightColor.a);
}
