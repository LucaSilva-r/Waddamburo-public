$input a_position
$output v_texcoord0

#include <bgfx_shader.sh>

// Full-target triangle. v = (1 - y) / 2 reads the model target top-down on every
// backend (GL's bottom-up render targets cancel out between the two passes).
void main()
{
    gl_Position = vec4(a_position, 0.0, 1.0);
    v_texcoord0 = vec2(a_position.x * 0.5 + 0.5, 0.5 - a_position.y * 0.5);
}
