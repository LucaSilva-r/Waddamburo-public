#include "waddamburo/texture.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#define CHECK(condition) do { if (!(condition)) { fprintf(stderr, "check failed: %s\n", #condition); return 1; } } while (0)

int main(void)
{
    uint8_t rgba[8 * 4 * 4];
    uint8_t blocks[8 * 4];
    uint8_t decoded[8 * 4 * 4];
    for (int i = 0; i < 8 * 4; i++)
    {
        rgba[i * 4] = (uint8_t)(i * 8);
        rgba[i * 4 + 1] = (uint8_t)(255 - i * 8);
        rgba[i * 4 + 2] = 128;
        rgba[i * 4 + 3] = (uint8_t)(i < 16 ? 255 : 128);
    }
    CHECK(waddamburo_texture_get_abi_version() == WADDAMBURO_TEXTURE_ABI_VERSION_1_0);
    CHECK(waddamburo_texture_encode_bc7(rgba, 6, 4, 16, blocks) == WADDAMBURO_TEXTURE_ERROR_INVALID_ARGUMENT);
    CHECK(waddamburo_texture_encode_bc7(NULL, 8, 4, 16, blocks) == WADDAMBURO_TEXTURE_ERROR_INVALID_ARGUMENT);
    CHECK(waddamburo_texture_encode_bc7(rgba, 8, 4, 16, blocks) == WADDAMBURO_TEXTURE_OK);
    CHECK(waddamburo_texture_decode_bc7(blocks, 8, 4, decoded) == WADDAMBURO_TEXTURE_OK);
    /* Lossy: the round trip stays close on average. */
    long error = 0;
    for (int i = 0; i < 8 * 4 * 4; i++)
        error += abs(decoded[i] - rgba[i]);
    CHECK(error / (8 * 4 * 4) <= 4);
    return 0;
}
