#ifndef WADDAMBURO_TEXTURE_H
#define WADDAMBURO_TEXTURE_H

#include <stdint.h>

#if defined(_WIN32)
#define WADDAMBURO_TEXTURE_CALL __cdecl
#if defined(WADDAMBURO_TEXTURE_BUILDING_LIBRARY)
#define WADDAMBURO_TEXTURE_API __declspec(dllexport)
#else
#define WADDAMBURO_TEXTURE_API __declspec(dllimport)
#endif
#else
#define WADDAMBURO_TEXTURE_CALL
#define WADDAMBURO_TEXTURE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define WADDAMBURO_TEXTURE_ABI_VERSION_1_0 UINT32_C(0x00010000)

typedef int32_t waddamburo_texture_result;
#define WADDAMBURO_TEXTURE_OK ((waddamburo_texture_result)0)
#define WADDAMBURO_TEXTURE_ERROR_INVALID_ARGUMENT ((waddamburo_texture_result)1)

WADDAMBURO_TEXTURE_API uint32_t WADDAMBURO_TEXTURE_CALL waddamburo_texture_get_abi_version(void);

/*
 * RGBA8 (width * height * 4 bytes, rows packed) to BC7 blocks (width * height bytes, block rows
 * top to bottom). Width and height must be multiples of 4. Partitions: 0 (fastest) to 64; 16 is a
 * good balance. Thread-safe; call it from several threads for throughput.
 */
WADDAMBURO_TEXTURE_API waddamburo_texture_result WADDAMBURO_TEXTURE_CALL waddamburo_texture_encode_bc7(
    const uint8_t *rgba, uint32_t width, uint32_t height, uint32_t partitions, uint8_t *blocks);

/* BC7 blocks back to RGBA8, for renderers without BC7 support. Same layout rules. */
WADDAMBURO_TEXTURE_API waddamburo_texture_result WADDAMBURO_TEXTURE_CALL waddamburo_texture_decode_bc7(
    const uint8_t *blocks, uint32_t width, uint32_t height, uint8_t *rgba);

#ifdef __cplusplus
}
#endif

#endif
