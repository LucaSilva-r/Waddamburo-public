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
#define WADDAMBURO_TEXT_ABI_VERSION_1_5 UINT32_C(0x00010005)
#define WADDAMBURO_TEXT_ABI_VERSION_1_6 UINT32_C(0x00010006)
#define WADDAMBURO_TEXT_ABI_VERSION_1_7 UINT32_C(0x00010007)
#define WADDAMBURO_TEXT_ABI_VERSION_1_8 UINT32_C(0x00010008)
#define WADDAMBURO_TEXT_ABI_VERSION_1_9 UINT32_C(0x00010009)

/* Song-title flags (ABI 1.5): a column too long for its slot keeps its font size and is squashed
 * vertically (as the arcade does), instead of the whole font shrinking. Compact and expanded only. */
#define WADDAMBURO_TEXT_SONG_TITLE_SQUASH UINT32_C(1)

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

/* Contexts retain one FreeType library and up to four font faces. utf8_font_path is the font,
 * optionally followed by fallback fonts, separated by '\n' (ABI 1.7): each scalar is drawn by
 * the first face that has it. Calls using the same context must be serialized by the caller. */
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

/* As above, with WADDAMBURO_TEXT_SONG_TITLE_* flags (ABI 1.5). */
WADDAMBURO_TEXT_API waddamburo_text_result WADDAMBURO_TEXT_CALL
waddamburo_text_context_render_song_title_ex_rgba8(
    waddamburo_text_context *context,
    const char *utf8_title,
    const char *utf8_subtitle,
    waddamburo_text_profile profile,
    uint32_t flags,
    uint32_t outline_rgb,
    uint32_t raster_scale,
    uint32_t width,
    uint32_t height,
    uint8_t *rgba8,
    uint64_t rgba8_capacity,
    waddamburo_text_error *error);

/* One glyph's masks and metrics (ABI 1.6). Pixels; y grows down from the top edge. */
typedef struct waddamburo_text_glyph {
    uint32_t struct_size;
    uint32_t width;   /* mask size: the glyph plus the outline radius on every side */
    uint32_t height;
    int32_t left;     /* the mask's left edge, from the pen position */
    int32_t top;      /* the mask's top edge, above the baseline */
    float advance;    /* pen advance */
    float ascender;   /* the font's line box scaled to one em (descender negative; ABI 1.7) */
    float descender;
} waddamburo_text_glyph;

/* Renders one Unicode scalar at font_px as two width*height 8-bit masks (ABI 1.6): the fill and its
 * outline (the fill grown round by outline_radius, as the song titles' border). With null masks or a
 * capacity under width*height, only `glyph` is filled (call again with buffers that size). */
WADDAMBURO_TEXT_API waddamburo_text_result WADDAMBURO_TEXT_CALL
waddamburo_text_context_render_glyph(
    waddamburo_text_context *context,
    uint32_t scalar,
    float font_px,
    uint32_t outline_radius,
    waddamburo_text_glyph *glyph,
    uint8_t *fill,
    uint8_t *outline,
    uint64_t mask_capacity,
    waddamburo_text_error *error);


/* Styled text (ABI 1.8): any text in a box, laid out and drawn as a stack of layers.
 *
 * A layer is the text's shape grown outwards by `grow` pixels (0: the glyphs themselves), moved by
 * the offset, blurred, and painted with a colour that runs from `top_rgba` to `bottom_rgba` over each
 * line (0xRRGGBBAA, straight alpha). Layers are drawn back to front, so a title with a shadow, a
 * black border, a red border and a white fill is four layers. Outlines are grown from the whole
 * text, so overlapping glyphs share one border. */
typedef struct waddamburo_text_layer {
    float grow;
    float offset_x;
    float offset_y;
    float blur;
    uint32_t top_rgba;
    uint32_t bottom_rgba;
    /* The gradient (ABI 1.9): its direction in degrees (0: top to bottom, 90: left to right), where along it
     * top_rgba ends and bottom_rgba begins (0..1; both 0: 0 and 1), over each line (0) or the whole text (1). */
    float gradient_angle;
    float gradient_from;
    float gradient_to;
    uint32_t gradient_over;
} waddamburo_text_layer;

#define WADDAMBURO_TEXT_GRADIENT_LINE UINT32_C(0)
#define WADDAMBURO_TEXT_GRADIENT_TEXT UINT32_C(1)

#define WADDAMBURO_TEXT_ALIGN_START UINT32_C(0)   /* left, or top */
#define WADDAMBURO_TEXT_ALIGN_CENTER UINT32_C(1)
#define WADDAMBURO_TEXT_ALIGN_END UINT32_C(2)     /* right, or bottom */

/* What a text too big for its box does. */
#define WADDAMBURO_TEXT_FIT_CLIP UINT32_C(0)      /* nothing: it runs out of the box */
#define WADDAMBURO_TEXT_FIT_SHRINK UINT32_C(1)    /* the whole font shrinks */
#define WADDAMBURO_TEXT_FIT_SQUEEZE UINT32_C(2)   /* squeezed along the line only (as the game's titles) */
#define WADDAMBURO_TEXT_FIT_WRAP UINT32_C(3)      /* broken into lines, then shrunk if still too big */

typedef struct waddamburo_text_style {
    uint32_t struct_size;
    float font_px;          /* the em size */
    float letter_spacing;   /* pixels added between characters (negative: tighter) */
    float line_height;      /* line (or column) advance as a multiple of the font's line box; 0: 1 */
    float slant;            /* horizontal shear per pixel of height (0.2: a synthetic italic) */
    uint32_t align;         /* along the line: WADDAMBURO_TEXT_ALIGN_* */
    uint32_t vertical_align;/* across the lines */
    uint32_t fit;           /* WADDAMBURO_TEXT_FIT_* */
    uint32_t vertical;      /* 1: top to bottom, columns right to left (punctuation turned) */
    uint32_t layer_count;   /* 1..16 */
    const waddamburo_text_layer *layers;
} waddamburo_text_style;

/* Renders utf8_text (newlines break lines) into a width x height premultiplied RGBA8 surface. All the
 * style's lengths are multiplied by `scale` (the drawable's pixel density over the logical box). The
 * text is fitted to the surface less the layers' reach (grow + blur + offset) and a pixel on every
 * side: a caller wanting the text to fill a box makes the surface that much larger. An empty text
 * gives a transparent surface. */
WADDAMBURO_TEXT_API waddamburo_text_result WADDAMBURO_TEXT_CALL
waddamburo_text_context_render_styled_rgba8(
    waddamburo_text_context *context,
    const char *utf8_text,
    const waddamburo_text_style *style,
    float scale,
    uint32_t width,
    uint32_t height,
    uint8_t *rgba8,
    uint64_t rgba8_capacity,
    waddamburo_text_error *error);

#ifdef __cplusplus
}
#endif

#endif
