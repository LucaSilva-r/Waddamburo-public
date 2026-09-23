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

#define WADDAMBURO_TEXT_ABI_VERSION_1_3 UINT32_C(0x00010003)
#define WADDAMBURO_TEXT_ABI_VERSION_1_4 UINT32_C(0x00010004)

typedef struct waddamburo_text_context waddamburo_text_context;

typedef int32_t waddamburo_text_result;
#define WADDAMBURO_TEXT_OK ((waddamburo_text_result)0)
#define WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT ((waddamburo_text_result)1)
#define WADDAMBURO_TEXT_ERROR_FONT ((waddamburo_text_result)2)
#define WADDAMBURO_TEXT_ERROR_TEXT ((waddamburo_text_result)3)
#define WADDAMBURO_TEXT_ERROR_MEMORY ((waddamburo_text_result)4)

typedef uint32_t waddamburo_text_profile;
#define WADDAMBURO_TEXT_PROFILE_SONG_COMPACT ((waddamburo_text_profile)0)
#define WADDAMBURO_TEXT_PROFILE_SONG_EXPANDED ((waddamburo_text_profile)1)
#define WADDAMBURO_TEXT_PROFILE_TRANSITION ((waddamburo_text_profile)2)
/* Gameplay song_info title: 720x64, fixed height, right-aligned, squeezed horizontally to fit. */
#define WADDAMBURO_TEXT_PROFILE_GAMEPLAY_TITLE ((waddamburo_text_profile)3)

typedef struct waddamburo_text_error {
    uint32_t struct_size;
    waddamburo_text_result code;
    int32_t native_code;
    uint32_t message_length;
    char message[256];
} waddamburo_text_error;

WADDAMBURO_TEXT_API uint32_t WADDAMBURO_TEXT_CALL waddamburo_text_get_abi_version(void);

/* Contexts retain one FreeType library and font face. Calls using the same
 * context must be serialized by the caller. */
WADDAMBURO_TEXT_API waddamburo_text_context *WADDAMBURO_TEXT_CALL
waddamburo_text_context_create(
    const char *utf8_font_path,
    waddamburo_text_error *error);

WADDAMBURO_TEXT_API void WADDAMBURO_TEXT_CALL
waddamburo_text_context_destroy(waddamburo_text_context *context);

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

/* Calibrated title profiles. Dimensions must be the profile's authored size
 * multiplied by raster_scale (compact 56x400, expanded 96x400, transition 720x103). */
WADDAMBURO_TEXT_API waddamburo_text_result WADDAMBURO_TEXT_CALL
waddamburo_text_render_song_title_rgba8(
    const char *utf8_font_path,
    const char *utf8_title,
    const char *utf8_subtitle,
    waddamburo_text_profile profile,
    uint32_t outline_rgb,
    uint32_t raster_scale,
    uint32_t width,
    uint32_t height,
    uint8_t *rgba8,
    uint64_t rgba8_capacity,
    waddamburo_text_error *error);

WADDAMBURO_TEXT_API waddamburo_text_result WADDAMBURO_TEXT_CALL
waddamburo_text_context_render_song_title_rgba8(
    waddamburo_text_context *context,
    const char *utf8_title,
    const char *utf8_subtitle,
    waddamburo_text_profile profile,
    uint32_t outline_rgb,
    uint32_t raster_scale,
    uint32_t width,
    uint32_t height,
    uint8_t *rgba8,
    uint64_t rgba8_capacity,
    waddamburo_text_error *error);

#ifdef __cplusplus
}
#endif

#endif
