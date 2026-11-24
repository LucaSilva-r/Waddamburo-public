$input v_texcoord0, v_color0, v_color1

#include <bgfx_shader.sh>

SAMPLER2D(s_texture, 0);
uniform vec4 u_luma;

// A luma key (background videos of light on black): the brightest channel is opacity, alpha = 1 - (1 - b)^curve
// (u_luma.x), and the colour is divided back by it, so over black the texel looks as it was. Then the quad's colour
// transform, as fs_quad; premultiplied alpha out.
void main()
{
    vec4 texel = texture2D(s_texture, v_texcoord0);
    float brightness = max(texel.r, max(texel.g, texel.b));
    float alpha = 1.0 - pow(1.0 - brightness, u_luma.x);
    vec4 keyed = vec4(texel.rgb / max(alpha, 0.0001), texel.a * alpha);
    vec4 straightColor = clamp(keyed * v_color0 + v_color1, 0.0, 1.0);
    gl_FragColor = vec4(straightColor.rgb * straightColor.a, straightColor.a);
}
