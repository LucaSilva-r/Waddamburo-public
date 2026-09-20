#include "waddamburo/text.h"

#include <ft2build.h>
#include FT_FREETYPE_H

#include <stddef.h>
#include <stdlib.h>
#include <string.h>

static int error_is_valid(const waddamburo_text_error *error)
{
    return error != NULL && error->struct_size >= sizeof(*error);
}

static waddamburo_text_result set_error(
    waddamburo_text_error *error,
    waddamburo_text_result code,
    int native_code,
    const char *message)
{
    size_t length;
    if (!error_is_valid(error))
        return WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT;
    length = strlen(message);
    if (length >= sizeof(error->message))
        length = sizeof(error->message) - 1U;
    error->code = code;
    error->native_code = native_code;
    error->message_length = (uint32_t)length;
    memcpy(error->message, message, length);
    error->message[length] = '\0';
    return code;
}

static int decode_utf8(const uint8_t **cursor, uint32_t *scalar)
{
    const uint8_t *p = *cursor;
    uint32_t value;
    uint32_t minimum;
    uint32_t continuation_count;
    uint32_t i;

    if (*p < UINT8_C(0x80)) {
        *scalar = *p;
        *cursor = p + 1;
        return *p != 0U;
    }
    if ((*p & UINT8_C(0xe0)) == UINT8_C(0xc0)) {
        value = (uint32_t)(*p & UINT8_C(0x1f));
        continuation_count = 1U;
        minimum = UINT32_C(0x80);
    } else if ((*p & UINT8_C(0xf0)) == UINT8_C(0xe0)) {
        value = (uint32_t)(*p & UINT8_C(0x0f));
        continuation_count = 2U;
        minimum = UINT32_C(0x800);
    } else if ((*p & UINT8_C(0xf8)) == UINT8_C(0xf0)) {
        value = (uint32_t)(*p & UINT8_C(0x07));
        continuation_count = 3U;
        minimum = UINT32_C(0x10000);
    } else {
        return 0;
    }
    p++;
    for (i = 0U; i < continuation_count; ++i) {
        if ((p[i] & UINT8_C(0xc0)) != UINT8_C(0x80))
            return 0;
        value = (value << 6U) | (uint32_t)(p[i] & UINT8_C(0x3f));
    }
    if (value < minimum || value > UINT32_C(0x10ffff) ||
        (value >= UINT32_C(0xd800) && value <= UINT32_C(0xdfff)))
        return 0;
    *scalar = value;
    *cursor = p + continuation_count;
    return 1;
}

static uint8_t bitmap_at(const FT_Bitmap *bitmap, uint32_t x, uint32_t y)
{
    const unsigned char *row;
    if (bitmap->pitch >= 0)
        row = bitmap->buffer + (size_t)y * (size_t)bitmap->pitch;
    else
        row = bitmap->buffer + (size_t)(bitmap->rows - 1U - y) * (size_t)(-bitmap->pitch);
    return row[x];
}

uint32_t WADDAMBURO_TEXT_CALL waddamburo_text_get_abi_version(void)
{
    return WADDAMBURO_TEXT_ABI_VERSION_1_0;
}

waddamburo_text_result WADDAMBURO_TEXT_CALL waddamburo_text_render_vertical_rgba8(
    const char *utf8_font_path,
    const char *utf8_text,
    uint32_t width,
    uint32_t height,
    uint8_t *rgba8,
    uint64_t rgba8_capacity,
    waddamburo_text_error *error)
{
    FT_Library library = NULL;
    FT_Face face = NULL;
    FT_Error ft_error;
    const uint8_t *cursor;
    uint32_t scalar;
    uint32_t glyph_count = 0U;
    uint32_t glyph_index = 0U;
    uint32_t pixel_size;
    uint32_t radius;
    uint64_t pixel_count;
    uint8_t *mask = NULL;
    uint8_t *outline = NULL;

    if (!error_is_valid(error) || utf8_font_path == NULL || utf8_font_path[0] == '\0' ||
        utf8_text == NULL || utf8_text[0] == '\0' || width == 0U || height == 0U || rgba8 == NULL)
        return set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "Invalid text rasterizer arguments.");
    pixel_count = (uint64_t)width * (uint64_t)height;
    if (pixel_count > UINT64_MAX / 4U || rgba8_capacity != pixel_count * 4U || pixel_count > SIZE_MAX)
        return set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "RGBA8 buffer size does not match the surface.");

    cursor = (const uint8_t *)utf8_text;
    while (*cursor != 0U) {
        if (!decode_utf8(&cursor, &scalar))
            return set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Title is not valid UTF-8.");
        glyph_count++;
    }
    if (glyph_count == 0U)
        return set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Title is empty.");

    mask = (uint8_t *)calloc((size_t)pixel_count, 1U);
    outline = (uint8_t *)calloc((size_t)pixel_count, 1U);
    if (mask == NULL || outline == NULL) {
        free(mask);
        free(outline);
        return set_error(error, WADDAMBURO_TEXT_ERROR_MEMORY, 0, "Unable to allocate the title surface.");
    }

    ft_error = FT_Init_FreeType(&library);
    if (ft_error != 0)
        goto font_failure;
    ft_error = FT_New_Face(library, utf8_font_path, 0, &face);
    if (ft_error != 0)
        goto font_failure;
    pixel_size = (uint32_t)(((uint64_t)height * 9U) / ((uint64_t)glyph_count * 10U));
    if (pixel_size > width * 3U / 4U)
        pixel_size = width * 3U / 4U;
    if (pixel_size < 6U)
        pixel_size = 6U;
    ft_error = FT_Set_Pixel_Sizes(face, 0U, pixel_size);
    if (ft_error != 0)
        goto font_failure;

    cursor = (const uint8_t *)utf8_text;
    while (*cursor != 0U) {
        FT_Bitmap *bitmap;
        int left;
        int top;
        uint32_t x;
        uint32_t y;
        (void)decode_utf8(&cursor, &scalar);
        ft_error = FT_Load_Char(face, scalar, FT_LOAD_RENDER | FT_LOAD_TARGET_NORMAL);
        if (ft_error != 0)
            goto font_failure;
        bitmap = &face->glyph->bitmap;
        left = ((int)width - (int)bitmap->width) / 2;
        top = (int)(((uint64_t)glyph_index * height) / glyph_count) +
            ((int)(height / glyph_count) - (int)bitmap->rows) / 2;
        for (y = 0U; y < bitmap->rows; ++y) {
            int destination_y = top + (int)y;
            if (destination_y < 0 || destination_y >= (int)height)
                continue;
            for (x = 0U; x < bitmap->width; ++x) {
                int destination_x = left + (int)x;
                uint8_t coverage;
                size_t offset;
                if (destination_x < 0 || destination_x >= (int)width)
                    continue;
                coverage = bitmap_at(bitmap, x, y);
                offset = (size_t)destination_y * width + (uint32_t)destination_x;
                if (coverage > mask[offset])
                    mask[offset] = coverage;
            }
        }
        glyph_index++;
    }

    radius = pixel_size / 11U;
    if (radius < 1U)
        radius = 1U;
    for (uint32_t y = 0U; y < height; ++y) {
        for (uint32_t x = 0U; x < width; ++x) {
            uint8_t maximum = 0U;
            uint32_t min_y = y > radius ? y - radius : 0U;
            uint32_t max_y = y + radius < height ? y + radius : height - 1U;
            uint32_t min_x = x > radius ? x - radius : 0U;
            uint32_t max_x = x + radius < width ? x + radius : width - 1U;
            for (uint32_t oy = min_y; oy <= max_y; ++oy) {
                for (uint32_t ox = min_x; ox <= max_x; ++ox) {
                    uint8_t value = mask[(size_t)oy * width + ox];
                    if (value > maximum)
                        maximum = value;
                }
            }
            outline[(size_t)y * width + x] = maximum;
        }
    }
    for (uint64_t i = 0U; i < pixel_count; ++i) {
        uint8_t fill = mask[i];
        rgba8[i * 4U] = fill;
        rgba8[i * 4U + 1U] = fill;
        rgba8[i * 4U + 2U] = fill;
        rgba8[i * 4U + 3U] = outline[i] > fill ? outline[i] : fill;
    }

    FT_Done_Face(face);
    FT_Done_FreeType(library);
    free(outline);
    free(mask);
    return set_error(error, WADDAMBURO_TEXT_OK, 0, "");

font_failure:
    if (face != NULL)
        FT_Done_Face(face);
    if (library != NULL)
        FT_Done_FreeType(library);
    free(outline);
    free(mask);
    return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not rasterize the requested title.");
}
