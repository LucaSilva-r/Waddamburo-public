#include "waddamburo/texture.h"

#include "bc7decomp.h"
#include "bc7enc.h"

#include <mutex>

namespace {

std::once_flag initialized;

bool valid(const void *pixels, uint32_t width, uint32_t height, const void *out)
{
    return pixels != nullptr && out != nullptr && width > 0 && height > 0
        && width % 4 == 0 && height % 4 == 0 && width <= 16384 && height <= 16384;
}

}

extern "C" uint32_t WADDAMBURO_TEXTURE_CALL waddamburo_texture_get_abi_version(void)
{
    return WADDAMBURO_TEXTURE_ABI_VERSION_1_0;
}

extern "C" waddamburo_texture_result WADDAMBURO_TEXTURE_CALL waddamburo_texture_encode_bc7(
    const uint8_t *rgba, uint32_t width, uint32_t height, uint32_t partitions, uint8_t *blocks)
{
    if (!valid(rgba, width, height, blocks) || partitions > BC7ENC_MAX_PARTITIONS)
        return WADDAMBURO_TEXTURE_ERROR_INVALID_ARGUMENT;
    std::call_once(initialized, bc7enc_compress_block_init);
    bc7enc_compress_block_params params;
    bc7enc_compress_block_params_init(&params);
    params.m_max_partitions = partitions;
    params.m_uber_level = 0;
    params.m_try_least_squares = true;
    const uint32_t columns = width / 4;
    for (uint32_t by = 0; by < height / 4; by++)
        for (uint32_t bx = 0; bx < columns; bx++)
        {
            uint8_t block[64];
            for (uint32_t y = 0; y < 4; y++)
                for (uint32_t x = 0; x < 16; x++)
                    block[y * 16 + x] = rgba[((by * 4 + y) * width + bx * 4) * 4 + x];
            bc7enc_compress_block(blocks + (by * columns + bx) * 16, block, &params);
        }
    return WADDAMBURO_TEXTURE_OK;
}

extern "C" waddamburo_texture_result WADDAMBURO_TEXTURE_CALL waddamburo_texture_decode_bc7(
    const uint8_t *blocks, uint32_t width, uint32_t height, uint8_t *rgba)
{
    if (!valid(blocks, width, height, rgba))
        return WADDAMBURO_TEXTURE_ERROR_INVALID_ARGUMENT;
    const uint32_t columns = width / 4;
    for (uint32_t by = 0; by < height / 4; by++)
        for (uint32_t bx = 0; bx < columns; bx++)
        {
            bc7decomp::color_rgba block[16];
            bc7decomp::unpack_bc7(blocks + (by * columns + bx) * 16, block);
            for (uint32_t y = 0; y < 4; y++)
                for (uint32_t x = 0; x < 4; x++)
                {
                    uint8_t *pixel = rgba + ((by * 4 + y) * width + bx * 4 + x) * 4;
                    const bc7decomp::color_rgba &value = block[y * 4 + x];
                    pixel[0] = value.r;
                    pixel[1] = value.g;
                    pixel[2] = value.b;
                    pixel[3] = value.a;
                }
        }
    return WADDAMBURO_TEXTURE_OK;
}
