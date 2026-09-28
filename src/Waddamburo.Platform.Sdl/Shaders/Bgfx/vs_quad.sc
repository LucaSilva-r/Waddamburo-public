$input a_position, a_texcoord0, a_color0, a_color1
$output v_texcoord0, v_color0, v_color1

#include <bgfx_shader.sh>

// Positions are normalized to the viewport, (0, 0) top-left.
void main()
{
    gl_Position = vec4(a_position.x * 2.0 - 1.0, 1.0 - a_position.y * 2.0, 0.0, 1.0);
    v_texcoord0 = a_texcoord0;
    v_color0 = a_color0;
    v_color1 = a_color1;
}
