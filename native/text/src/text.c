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

static int scalar_in_list(uint32_t scalar, const uint32_t *values, size_t count)
{
    size_t i;
    for (i = 0U; i < count; ++i) {
        if (values[i] == scalar)
            return 1;
    }
    return 0;
}

static int is_beside_scalar(uint32_t scalar)
{
    static const uint32_t values[] = { '.', ',', '\'', '"', 0x3002U, 0x3001U };
    return scalar_in_list(scalar, values, sizeof(values) / sizeof(values[0]));
}

static int is_horizontal_group_scalar(uint32_t scalar)
{
    static const uint32_t values[] = { '!', '?', 0xff01U, 0xff1fU, 0x2020U };
    return scalar_in_list(scalar, values, sizeof(values) / sizeof(values[0]));
}

static int has_reduced_leading(uint32_t scalar)
{
    static const uint32_t values[] = {
        ' ', 'a', 'c', 'e', 'g', 'm', 'n', 'o', 'p', 'q', 'r', 's', 'u', 'v', 'w', 'x', 'y', 'z',
        0x3041U, 0x3043U, 0x3045U, 0x3047U, 0x3049U, 0x3063U, 0x3083U, 0x3085U,
        0x3087U, 0x308eU, 0x3095U, 0x3096U, 0x30a1U, 0x30a3U, 0x30a5U, 0x30a7U,
        0x30a9U, 0x30c3U, 0x30e3U, 0x30e5U, 0x30e7U, 0x30eeU, 0x30f5U, 0x30f6U,
    };
    return scalar_in_list(scalar, values, sizeof(values) / sizeof(values[0]));
}

static int is_rotated_scalar(uint32_t scalar)
{
    static const uint32_t values[] = {
        '-', 0x2010U, '|', '/', '\\', 0x30fcU, 0xff5eU, '~', 0xff08U, 0xff09U,
        '(', ')', 0x300cU, 0x300dU, '[', ']', 0x3010U, 0x3011U, 0x2026U,
        0x2192U, ':', 0xff1aU,
    };
    return scalar_in_list(scalar, values, sizeof(values) / sizeof(values[0]));
}

static void mask_pixel(uint8_t *mask, uint32_t width, uint32_t height, int x, int y, uint8_t coverage)
{
    size_t offset;
    if (x < 0 || y < 0 || x >= (int)width || y >= (int)height)
        return;
    offset = (size_t)y * width + (uint32_t)x;
    if (coverage > mask[offset])
        mask[offset] = coverage;
}

static void draw_bitmap_to_mask(
    uint8_t *mask,
    uint32_t width,
    uint32_t height,
    const FT_Bitmap *bitmap,
    int left,
    int top,
    int rotate_clockwise)
{
    uint32_t x;
    uint32_t y;
    for (y = 0U; y < bitmap->rows; ++y) {
        for (x = 0U; x < bitmap->width; ++x) {
            uint8_t coverage = bitmap_at(bitmap, x, y);
            if (!coverage)
                continue;
            if (rotate_clockwise) {
                mask_pixel(mask, width, height,
                    left + (int)bitmap->rows - 1 - (int)y,
                    top + (int)x,
                    coverage);
            } else {
                mask_pixel(mask, width, height, left + (int)x, top + (int)y, coverage);
            }
        }
    }
}

typedef struct title_item {
    uint32_t first;
    uint32_t count;
    int horizontal_group;
} title_item;

static waddamburo_text_result decode_title_items(
    const char *text,
    uint32_t scalars[256],
    title_item items[256],
    uint32_t *item_count,
    waddamburo_text_error *error)
{
    const uint8_t *cursor = (const uint8_t *)text;
    uint32_t scalar_count = 0U;
    uint32_t count = 0U;
    while (*cursor != 0U) {
        if (scalar_count == 256U)
            return set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Song title exceeds 256 Unicode scalars.");
        if (!decode_utf8(&cursor, &scalars[scalar_count]))
            return set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Song title is not valid UTF-8.");
        scalar_count++;
    }
    for (uint32_t i = 0U; i < scalar_count;) {
        uint32_t run = 1U;
        if (is_horizontal_group_scalar(scalars[i])) {
            while (i + run < scalar_count && run < 8U && is_horizontal_group_scalar(scalars[i + run]))
                run++;
        }
        items[count].first = i;
        items[count].count = run;
        items[count].horizontal_group = run > 1U;
        count++;
        i += run;
    }
    *item_count = count;
    return WADDAMBURO_TEXT_OK;
}

static waddamburo_text_result render_title_column(
    FT_Face face,
    const char *text,
    float requested_font_px,
    float requested_leading_px,
    float center_x,
    uint32_t top,
    uint32_t bottom,
    uint8_t *mask,
    uint32_t width,
    uint32_t height,
    waddamburo_text_error *error)
{
    uint32_t scalars[256];
    title_item items[256];
    float positions[256];
    uint32_t item_count = 0U;
    float font_px = requested_font_px;
    float leading = requested_leading_px;
    FT_Error ft_error;
    float ascent;
    float natural_height;
    waddamburo_text_result result = decode_title_items(text, scalars, items, &item_count, error);
    if (result != WADDAMBURO_TEXT_OK || item_count == 0U)
        return result;

    positions[0] = 0.0f;
    for (uint32_t i = 1U; i < item_count; ++i) {
        uint32_t scalar = scalars[items[i].first];
        float step = is_beside_scalar(scalar) ? leading * 0.3f
            : has_reduced_leading(scalar) ? leading * 0.8f
            : leading;
        positions[i] = positions[i - 1U] + step;
    }
    natural_height = positions[item_count - 1U] + font_px;
    if (natural_height > (float)(bottom - top)) {
        float fit = (float)(bottom - top) / natural_height;
        font_px *= fit;
        leading *= fit;
        positions[0] = 0.0f;
        for (uint32_t i = 1U; i < item_count; ++i) {
            uint32_t scalar = scalars[items[i].first];
            float step = is_beside_scalar(scalar) ? leading * 0.3f
                : has_reduced_leading(scalar) ? leading * 0.8f
                : leading;
            positions[i] = positions[i - 1U] + step;
        }
    }
    if (font_px < 6.0f)
        font_px = 6.0f;
    ft_error = FT_Set_Pixel_Sizes(face, 0U, (FT_UInt)(font_px + 0.5f));
    if (ft_error != 0)
        return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not size the song-title font.");
    ascent = (float)face->size->metrics.ascender / 64.0f;

    for (uint32_t i = 0U; i < item_count; ++i) {
        title_item item = items[i];
        float group_width = 0.0f;
        float x;
        for (uint32_t k = 0U; k < item.count; ++k) {
            ft_error = FT_Load_Char(face, scalars[item.first + k], FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL);
            if (ft_error != 0)
                return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not measure a song-title glyph.");
            group_width += (float)face->glyph->advance.x / 64.0f;
        }
        x = center_x - group_width * 0.5f;
        for (uint32_t k = 0U; k < item.count; ++k) {
            uint32_t scalar = scalars[item.first + k];
            FT_GlyphSlot glyph;
            int rotate;
            int left;
            int glyph_top;
            ft_error = FT_Load_Char(face, scalar, FT_LOAD_RENDER | FT_LOAD_TARGET_NORMAL);
            if (ft_error != 0)
                return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not rasterize a song-title glyph.");
            glyph = face->glyph;
            rotate = !is_beside_scalar(scalar) && is_rotated_scalar(scalar);
            if (rotate) {
                left = (int)(center_x - (float)glyph->bitmap.rows * 0.5f);
                glyph_top = (int)((float)top + positions[i] + (font_px - (float)glyph->bitmap.width) * 0.5f);
            } else {
                left = (int)x + glyph->bitmap_left;
                glyph_top = (int)((float)top + positions[i] + ascent) - glyph->bitmap_top;
                if (scalar == '\'' || scalar == '"')
                    glyph_top += (int)(font_px * 0.7f);
            }
            draw_bitmap_to_mask(mask, width, height, &glyph->bitmap, left, glyph_top, rotate);
            x += (float)glyph->advance.x / 64.0f;
        }
    }
    return WADDAMBURO_TEXT_OK;
}

uint32_t WADDAMBURO_TEXT_CALL waddamburo_text_get_abi_version(void)
{
    return WADDAMBURO_TEXT_ABI_VERSION_1_1;
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

waddamburo_text_result WADDAMBURO_TEXT_CALL waddamburo_text_render_song_title_rgba8(
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
    waddamburo_text_error *error)
{
    FT_Library library = NULL;
    FT_Face face = NULL;
    FT_Error ft_error;
    uint32_t base_width;
    uint32_t radius;
    uint64_t pixel_count;
    uint8_t *mask = NULL;
    uint8_t *outline = NULL;
    waddamburo_text_result result;

    if (!error_is_valid(error) || utf8_font_path == NULL || utf8_font_path[0] == '\0' ||
        utf8_title == NULL || utf8_title[0] == '\0' || rgba8 == NULL ||
        raster_scale == 0U || raster_scale > 4U || outline_rgb > UINT32_C(0x00ffffff) ||
        (profile != WADDAMBURO_TEXT_PROFILE_SONG_COMPACT &&
         profile != WADDAMBURO_TEXT_PROFILE_SONG_EXPANDED))
        return set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "Invalid song-title rasterizer arguments.");

    base_width = profile == WADDAMBURO_TEXT_PROFILE_SONG_COMPACT ? 56U : 96U;
    if (width != base_width * raster_scale || height != 400U * raster_scale)
        return set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "Song-title surface dimensions do not match its profile and scale.");
    pixel_count = (uint64_t)width * height;
    if (pixel_count > UINT64_MAX / 4U || rgba8_capacity != pixel_count * 4U || pixel_count > SIZE_MAX)
        return set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "RGBA8 buffer size does not match the song-title surface.");

    mask = (uint8_t *)calloc((size_t)pixel_count, 1U);
    outline = (uint8_t *)calloc((size_t)pixel_count, 1U);
    if (mask == NULL || outline == NULL) {
        free(mask);
        free(outline);
        return set_error(error, WADDAMBURO_TEXT_ERROR_MEMORY, 0, "Unable to allocate the song-title surface.");
    }

    ft_error = FT_Init_FreeType(&library);
    if (ft_error != 0)
        goto song_font_failure;
    ft_error = FT_New_Face(library, utf8_font_path, 0, &face);
    if (ft_error != 0)
        goto song_font_failure;

    if (profile == WADDAMBURO_TEXT_PROFILE_SONG_COMPACT) {
        result = render_title_column(
            face, utf8_title, 38.0f * raster_scale, 35.0f * raster_scale,
            28.0f * raster_scale, 5U * raster_scale, height - 5U * raster_scale,
            mask, width, height, error);
    } else {
        result = render_title_column(
            face, utf8_title, 38.0f * raster_scale, 35.0f * raster_scale,
            70.0f * raster_scale, 5U * raster_scale, height - 5U * raster_scale,
            mask, width, height, error);
        if (result == WADDAMBURO_TEXT_OK && utf8_subtitle != NULL && utf8_subtitle[0] != '\0') {
            result = render_title_column(
                face, utf8_subtitle, 30.5f * raster_scale, 29.7f * raster_scale,
                23.0f * raster_scale, 5U * raster_scale, height - 5U * raster_scale,
                mask, width, height, error);
        }
    }
    if (result != WADDAMBURO_TEXT_OK)
        goto song_failure;

    radius = (uint32_t)(4.5f * raster_scale + 0.5f);
    for (uint32_t y = 0U; y < height; ++y) {
        for (uint32_t x = 0U; x < width; ++x) {
            uint8_t maximum = 0U;
            int min_y = (int)y - (int)radius;
            int max_y = (int)y + (int)radius;
            int min_x = (int)x - (int)radius;
            int max_x = (int)x + (int)radius;
            if (min_y < 0) min_y = 0;
            if (min_x < 0) min_x = 0;
            if (max_y >= (int)height) max_y = (int)height - 1;
            if (max_x >= (int)width) max_x = (int)width - 1;
            for (int oy = min_y; oy <= max_y; ++oy) {
                int dy = oy - (int)y;
                for (int ox = min_x; ox <= max_x; ++ox) {
                    int dx = ox - (int)x;
                    uint8_t value;
                    if (dx * dx + dy * dy > (int)(radius * radius))
                        continue;
                    value = mask[(size_t)oy * width + (uint32_t)ox];
                    if (value > maximum)
                        maximum = value;
                }
            }
            outline[(size_t)y * width + x] = maximum;
        }
    }
    for (uint64_t i = 0U; i < pixel_count; ++i) {
        uint32_t fill = mask[i];
        uint32_t border = ((uint32_t)outline[i] * (255U - fill) + 127U) / 255U;
        uint32_t alpha = fill + border;
        uint32_t red = fill + ((((outline_rgb >> 16U) & 255U) * border + 127U) / 255U);
        uint32_t green = fill + ((((outline_rgb >> 8U) & 255U) * border + 127U) / 255U);
        uint32_t blue = fill + (((outline_rgb & 255U) * border + 127U) / 255U);
        rgba8[i * 4U] = (uint8_t)(red > 255U ? 255U : red);
        rgba8[i * 4U + 1U] = (uint8_t)(green > 255U ? 255U : green);
        rgba8[i * 4U + 2U] = (uint8_t)(blue > 255U ? 255U : blue);
        rgba8[i * 4U + 3U] = (uint8_t)(alpha > 255U ? 255U : alpha);
    }

    FT_Done_Face(face);
    FT_Done_FreeType(library);
    free(outline);
    free(mask);
    return set_error(error, WADDAMBURO_TEXT_OK, 0, "");

song_font_failure:
    result = set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not initialize the song-title font.");
song_failure:
    if (face != NULL)
        FT_Done_Face(face);
    if (library != NULL)
        FT_Done_FreeType(library);
    free(outline);
    free(mask);
    return result;
}
