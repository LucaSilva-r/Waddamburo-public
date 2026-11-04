$input v_texcoord0, v_color0, v_color1

#include <bgfx_shader.sh>

// One direction of a separable Gaussian blur over a premultiplied render target: nine taps in five
// fetches (linear sampling between texel pairs). With u_blur zero it is a plain copy. Colour 0 tints it.
SAMPLER2D(s_texture, 0);
// xy: one texel step in UV, along the blur's direction (spread included).
uniform vec4 u_blur;

void main()
{
    vec2 near = u_blur.xy * 1.3846153846;
    vec2 far = u_blur.xy * 3.2307692308;
    vec4 sum = texture2D(s_texture, v_texcoord0) * 0.2270270270
        + (texture2D(s_texture, v_texcoord0 + near) + texture2D(s_texture, v_texcoord0 - near)) * 0.3162162162
        + (texture2D(s_texture, v_texcoord0 + far) + texture2D(s_texture, v_texcoord0 - far)) * 0.0702702703;
    gl_FragColor = sum * v_color0;
}
