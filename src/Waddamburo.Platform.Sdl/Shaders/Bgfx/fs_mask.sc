$input v_texcoord0, v_color0, v_color1

#include <bgfx_shader.sh>

SAMPLER2D(s_texture, 0);

// Stencil-only pass: colour writes are masked off; transparent texels leave the stencil alone.
void main()
{
    vec4 straightColor = texture2D(s_texture, v_texcoord0) * v_color0 + v_color1;
    if (straightColor.a < 0.5)
        discard;
    gl_FragColor = vec4(straightColor.rgb * straightColor.a, straightColor.a);
}
