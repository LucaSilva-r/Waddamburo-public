$input v_texcoord0, v_color0, v_color1

#include <bgfx_shader.sh>

SAMPLER2D(s_texture, 0);

// Straight-alpha colour transform in, premultiplied alpha out. A multiply alpha below 0 (stored as -1 - alpha) marks an
// additive quad: its alpha is written as 0, so the normal blend (s + d(1 - sa)) adds it (s + d) and the two batch together.
void main()
{
    float additive = step(v_color0.a, -0.5);
    vec4 multiply = vec4(v_color0.rgb, mix(v_color0.a, -1.0 - v_color0.a, additive));
    vec4 straightColor = clamp(texture2D(s_texture, v_texcoord0) * multiply + v_color1, 0.0, 1.0);
    // Dither: under half a level of noise per pixel (interleaved gradient noise, fixed to the screen), so the 8-bit target
    // rounds a soft gradient up or down by it instead of in bands. A whole level stays itself.
    float noise = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))) - 0.5;
    gl_FragColor = vec4(straightColor.rgb * straightColor.a + noise * (0.98 / 255.0), straightColor.a * (1.0 - additive));
}
