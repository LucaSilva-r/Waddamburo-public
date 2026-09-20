#ifndef WADDAMBURO_TEXT_H
#define WADDAMBURO_TEXT_H

#include <stdint.h>

#if defined(_WIN32)
#define WADDAMBURO_TEXT_CALL __cdecl
#if defined(WADDAMBURO_TEXT_BUILDING_LIBRARY)
#define WADDAMBURO_TEXT_API __declspec(dllexport)
#else
#define WADDAMBURO_TEXT_API __declspec(dllimport)
#endif
#else
#define WADDAMBURO_TEXT_CALL
#define WADDAMBURO_TEXT_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define WADDAMBURO_TEXT_ABI_VERSION_1_0 UINT32_C(0x00010000)

typedef int32_t waddamburo_text_result;
#define WADDAMBURO_TEXT_OK ((waddamburo_text_result)0)
#define WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT ((waddamburo_text_result)1)
#define WADDAMBURO_TEXT_ERROR_FONT ((waddamburo_text_result)2)
#define WADDAMBURO_TEXT_ERROR_TEXT ((waddamburo_text_result)3)
#define WADDAMBURO_TEXT_ERROR_MEMORY ((waddamburo_text_result)4)

typedef struct waddamburo_text_error {
    uint32_t struct_size;
    waddamburo_text_result code;
    int32_t native_code;
    uint32_t message_length;
    char message[256];
} waddamburo_text_error;

WADDAMBURO_TEXT_API uint32_t WADDAMBURO_TEXT_CALL waddamburo_text_get_abi_version(void);

/* Produces premultiplied RGBA8. Each Unicode scalar is laid out top-to-bottom. */
WADDAMBURO_TEXT_API waddamburo_text_result WADDAMBURO_TEXT_CALL
waddamburo_text_render_vertical_rgba8(
    const char *utf8_font_path,
    const char *utf8_text,
    uint32_t width,
    uint32_t height,
    uint8_t *rgba8,
    uint64_t rgba8_capacity,
    waddamburo_text_error *error);

#ifdef __cplusplus
}
#endif

#endif
