#include "waddamburo/text.h"

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_GLYPH_H

#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

struct waddamburo_text_context {
    FT_Library library;
    FT_Face face;
};

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

/* One line of Felzenszwalb and Huttenlocher's squared Euclidean distance transform: d[q] =
 * min over p of (q - p)^2 + f[p], in linear time (the lower envelope of the parabolas). */
static void distance_1d(const float *f, float *d, uint32_t n, uint32_t *v, float *z)
{
    uint32_t k = 0U;
    v[0] = 0U;
    z[0] = -1e20f;
    z[1] = 1e20f;
    for (uint32_t q = 1U; q < n; ++q) {
        float s;
        for (;;) {
            float p = (float)v[k];
            s = ((f[q] + (float)q * (float)q) - (f[v[k]] + p * p)) / (2.0f * ((float)q - p));
            if (s > z[k] || k == 0U)
                break;
            k--;
        }
        if (s <= z[k]) {
            v[0] = q;
            z[0] = -1e20f;
            z[1] = 1e20f;
            k = 0U;
            continue;
        }
        k++;
        v[k] = q;
        z[k] = s;
        z[k + 1U] = 1e20f;
    }
    k = 0U;
    for (uint32_t q = 0U; q < n; ++q) {
        float dq;
        while (z[k + 1U] < (float)q)
            k++;
        dq = (float)q - (float)v[k];
        d[q] = dq * dq + f[v[k]];
    }
}

/* Outline the rendered fill with a round border: every pixel within radius of the ink (fill at half
 * coverage or more) is covered, with a one-pixel anti-aliased rim. Stroking font contours can leave
 * tiny self-intersection holes in the counters of outlined UI letters; a border grown from the fill
 * follows the same hinted pixels as the white fill, including when FreeType changes hinting at small
 * sizes. A distance transform costs the same at any radius (a round max filter was 2r+1 passes over
 * the surface: ~30 ms per label at raster scale 3), and only the ink's box grown by the radius is
 * touched (the rest stays zero). */
static int dilate_mask_round(
    const uint8_t *fill, uint8_t *outline, uint32_t width, uint32_t height, uint32_t radius)
{
    const float far_away = 1e20f;
    uint32_t ink_left = width, ink_right = 0U, ink_top = height, ink_bottom = 0U;
    uint32_t box_left, box_top, box_width, box_height, longest;
    float *grid, *line, *result, *z;
    uint32_t *v;
    for (uint32_t y = 0U; y < height; ++y)
        for (uint32_t x = 0U; x < width; ++x)
            if (fill[(size_t)y * width + x] >= 128U) {
                if (x < ink_left) ink_left = x;
                if (x > ink_right) ink_right = x;
                if (y < ink_top) ink_top = y;
                ink_bottom = y;
            }
    if (ink_left > ink_right) {
        memcpy(outline, fill, (size_t)width * height);
        return 1;
    }
    box_left = ink_left > radius + 1U ? ink_left - radius - 1U : 0U;
    box_top = ink_top > radius + 1U ? ink_top - radius - 1U : 0U;
    box_width = (ink_right + radius + 1U < width ? ink_right + radius + 1U : width - 1U) - box_left + 1U;
    box_height = (ink_bottom + radius + 1U < height ? ink_bottom + radius + 1U : height - 1U) - box_top + 1U;
    longest = box_width > box_height ? box_width : box_height;
    grid = (float *)malloc((size_t)box_width * box_height * sizeof(*grid));
    line = (float *)calloc((size_t)longest, sizeof(*line));
    result = (float *)calloc((size_t)longest, sizeof(*result));
    z = (float *)malloc(((size_t)longest + 1U) * sizeof(*z));
    v = (uint32_t *)malloc((size_t)longest * sizeof(*v));
    if (grid == NULL || line == NULL || result == NULL || z == NULL || v == NULL) {
        free(grid); free(line); free(result); free(z); free(v);
        return 0;
    }
    for (uint32_t y = 0U; y < box_height; ++y)
        for (uint32_t x = 0U; x < box_width; ++x)
            grid[(size_t)y * box_width + x] =
                fill[(size_t)(box_top + y) * width + box_left + x] >= 128U ? 0.0f : far_away;
    /* Columns, then rows: the squared distance to the nearest ink pixel. */
    for (uint32_t x = 0U; x < box_width; ++x) {
        for (uint32_t y = 0U; y < box_height; ++y)
            line[y] = grid[(size_t)y * box_width + x];
        distance_1d(line, result, box_height, v, z);
        for (uint32_t y = 0U; y < box_height; ++y)
            grid[(size_t)y * box_width + x] = result[y];
    }
    for (uint32_t y = 0U; y < box_height; ++y) {
        float *row = grid + (size_t)y * box_width;
        distance_1d(row, result, box_width, v, z);
        for (uint32_t x = 0U; x < box_width; ++x) {
            size_t index = (size_t)(box_top + y) * width + box_left + x;
            float cover = (float)radius + 0.5f - sqrtf(result[x]);
            uint32_t border = cover >= 1.0f ? 255U : cover <= 0.0f ? 0U : (uint32_t)(cover * 255.0f + 0.5f);
            /* Fill below half coverage (anti-aliased glyph edges) still counts at its own level. */
            outline[index] = (uint8_t)(border > fill[index] ? border : fill[index]);
        }
    }
    free(grid); free(line); free(result); free(z); free(v);
    return 1;
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
    int squash,
    int from_bottom,
    uint8_t *mask,
    uint32_t width,
    uint32_t height,
    waddamburo_text_error *error)
{
    uint32_t scalars[256];
    float vertical = 1.0f; /* < 1: the column squashed vertically to fit, at its full font size */
    float offset;
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
        if (squash)
            vertical = fit;
        else
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
    /* A column shorter than its slot starts at the bottom when asked (the expanded subtitle). */
    natural_height = positions[item_count - 1U] + font_px * vertical;
    offset = from_bottom && natural_height < (float)(bottom - top) ? (float)(bottom - top) - natural_height : 0.0f;
    top += (uint32_t)offset;
    ft_error = FT_Set_Pixel_Sizes(face, 0U, (FT_UInt)(font_px + 0.5f));
    if (ft_error != 0)
        return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not size the song-title font.");
    ascent = (float)face->size->metrics.ascender / 64.0f * vertical;

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
            FT_Glyph fill_glyph = NULL;
            FT_BitmapGlyph fill_bitmap;
            int rotate;
            int left;
            int glyph_top;
            rotate = !is_beside_scalar(scalar) && is_rotated_scalar(scalar);
            /* Squashed: glyphs scaled vertically only (a rotated one along its own x, which turns
             * vertical); the measuring above ran without it, and horizontal advances do not change. */
            if (vertical < 1.0f) {
                FT_Matrix squash_matrix;
                squash_matrix.xx = (FT_Fixed)((rotate ? vertical : 1.0f) * 65536.0f);
                squash_matrix.xy = 0;
                squash_matrix.yx = 0;
                squash_matrix.yy = (FT_Fixed)((rotate ? 1.0f : vertical) * 65536.0f);
                FT_Set_Transform(face, &squash_matrix, NULL);
            }
            ft_error = FT_Load_Char(face, scalar, FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL);
            if (vertical < 1.0f)
                FT_Set_Transform(face, NULL, NULL);
            if (ft_error != 0)
                return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not rasterize a song-title glyph.");
            ft_error = FT_Get_Glyph(face->glyph, &fill_glyph);
            if (ft_error != 0)
                return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not retain a song-title glyph.");
            ft_error = FT_Glyph_To_Bitmap(&fill_glyph, FT_RENDER_MODE_NORMAL, NULL, 1);
            if (ft_error != 0) {
                FT_Done_Glyph(fill_glyph);
                return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not render a song-title glyph.");
            }
            fill_bitmap = (FT_BitmapGlyph)fill_glyph;
            if (rotate) {
                left = (int)(center_x - (float)fill_bitmap->bitmap.rows * 0.5f);
                glyph_top = (int)((float)top + positions[i] + (font_px * vertical - (float)fill_bitmap->bitmap.width) * 0.5f);
            } else {
                int baseline_y = (int)((float)top + positions[i] + ascent);
                left = (int)x + fill_bitmap->left;
                glyph_top = baseline_y - fill_bitmap->top;
                if (scalar == '\'' || scalar == '"') {
                    glyph_top += (int)(font_px * vertical * 0.7f);
                }
            }
            draw_bitmap_to_mask(mask, width, height, &fill_bitmap->bitmap, left, glyph_top, rotate);
            x += (float)face->glyph->advance.x / 64.0f;
            FT_Done_Glyph(fill_glyph);
        }
    }
    return WADDAMBURO_TEXT_OK;
}

static waddamburo_text_result render_title_row(
    FT_Face face,
    const char *text,
    uint32_t raster_scale,
    uint8_t *mask,
    uint32_t width,
    uint32_t height,
    int gameplay,
    float base_px,
    float center_y,
    waddamburo_text_error *error)
{
    uint32_t scalars[256];
    uint32_t scalar_count = 0U;
    const uint8_t *cursor = (const uint8_t *)text;
    /* Transition: centred, whole font shrinks to fit. Gameplay: fixed height, right-aligned,
     * glyphs squeezed horizontally (FreeType transform) when the title is too wide. */
    float font_px = base_px * raster_scale;
    float margin = 6.0f * raster_scale;
    float advance = 0.0f;
    float pen_x;
    int baseline;
    FT_Error ft_error;

    while (*cursor != 0U) {
        if (scalar_count == 256U)
            return set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Transition title exceeds 256 Unicode scalars.");
        if (!decode_utf8(&cursor, &scalars[scalar_count]))
            return set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Transition title is not valid UTF-8.");
        scalar_count++;
    }
    if (scalar_count == 0U)
        return set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Transition title is empty.");

    ft_error = FT_Set_Pixel_Sizes(face, 0U, (FT_UInt)(font_px + 0.5f));
    if (ft_error != 0)
        return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not size the transition-title font.");
    for (uint32_t i = 0U; i < scalar_count; ++i) {
        ft_error = FT_Load_Char(face, scalars[i], FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL);
        if (ft_error != 0)
            return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not measure a transition-title glyph.");
        advance += (float)face->glyph->advance.x / 64.0f;
    }
    if (gameplay && advance > (float)width - 2.0f * margin) {
        FT_Matrix squeeze;
        squeeze.xx = (FT_Fixed)(((float)width - 2.0f * margin) / advance * 65536.0f);
        squeeze.xy = 0;
        squeeze.yx = 0;
        squeeze.yy = 0x10000;
        FT_Set_Transform(face, &squeeze, NULL); /* advances below are transformed too */
        advance = 0.0f;
        for (uint32_t i = 0U; i < scalar_count; ++i) {
            ft_error = FT_Load_Char(face, scalars[i], FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL);
            if (ft_error != 0)
                return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not measure a gameplay-title glyph.");
            advance += (float)face->glyph->advance.x / 64.0f;
        }
    } else if (!gameplay && advance > width * 0.94f) {
        font_px *= width * 0.94f / advance;
        if (font_px < 6.0f)
            font_px = 6.0f;
        ft_error = FT_Set_Pixel_Sizes(face, 0U, (FT_UInt)(font_px + 0.5f));
        if (ft_error != 0)
            return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not fit the transition-title font.");
        advance = 0.0f;
        for (uint32_t i = 0U; i < scalar_count; ++i) {
            ft_error = FT_Load_Char(face, scalars[i], FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL);
            if (ft_error != 0)
                return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not measure a transition-title glyph.");
            advance += (float)face->glyph->advance.x / 64.0f;
        }
    }

    pen_x = gameplay ? (float)width - margin - advance : ((float)width - advance) * 0.5f;
    (void)height;
    baseline = (int)(center_y * raster_scale
        + ((float)face->size->metrics.ascender / 64.0f
        + (float)face->size->metrics.descender / 64.0f) * 0.5f);
    for (uint32_t i = 0U; i < scalar_count; ++i) {
        FT_Glyph fill_glyph = NULL;
        FT_BitmapGlyph fill_bitmap;
        float glyph_advance;

        ft_error = FT_Load_Char(face, scalars[i], FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL);
        if (ft_error != 0)
            return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not rasterize a transition-title glyph.");
        glyph_advance = (float)face->glyph->advance.x / 64.0f;
        ft_error = FT_Get_Glyph(face->glyph, &fill_glyph);
        if (ft_error != 0)
            return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not retain a transition-title glyph.");
        ft_error = FT_Glyph_To_Bitmap(&fill_glyph, FT_RENDER_MODE_NORMAL, NULL, 1);
        if (ft_error != 0) {
            FT_Done_Glyph(fill_glyph);
            return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not render a transition-title glyph.");
        }
        fill_bitmap = (FT_BitmapGlyph)fill_glyph;
        draw_bitmap_to_mask(
            mask, width, height, &fill_bitmap->bitmap,
            (int)pen_x + fill_bitmap->left, baseline - fill_bitmap->top, 0);
        pen_x += glyph_advance;
        FT_Done_Glyph(fill_glyph);
    }
    return WADDAMBURO_TEXT_OK;
}

uint32_t WADDAMBURO_TEXT_CALL waddamburo_text_get_abi_version(void)
{
    return WADDAMBURO_TEXT_ABI_VERSION_1_6;
}

waddamburo_text_context *WADDAMBURO_TEXT_CALL waddamburo_text_context_create(
    const char *utf8_font_path,
    waddamburo_text_error *error)
{
    waddamburo_text_context *context;
    FT_Error ft_error;
    if (!error_is_valid(error) || utf8_font_path == NULL || utf8_font_path[0] == '\0') {
        set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "Invalid text context arguments.");
        return NULL;
    }
    context = (waddamburo_text_context *)calloc(1U, sizeof(*context));
    if (context == NULL) {
        set_error(error, WADDAMBURO_TEXT_ERROR_MEMORY, 0, "Unable to allocate the text context.");
        return NULL;
    }
    ft_error = FT_Init_FreeType(&context->library);
    if (ft_error == 0)
        ft_error = FT_New_Face(context->library, utf8_font_path, 0, &context->face);
    if (ft_error != 0) {
        if (context->library != NULL)
            FT_Done_FreeType(context->library);
        free(context);
        set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not initialize the text context.");
        return NULL;
    }
    set_error(error, WADDAMBURO_TEXT_OK, 0, "");
    return context;
}

void WADDAMBURO_TEXT_CALL waddamburo_text_context_destroy(waddamburo_text_context *context)
{
    if (context == NULL)
        return;
    FT_Done_Face(context->face);
    FT_Done_FreeType(context->library);
    free(context);
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

waddamburo_text_result WADDAMBURO_TEXT_CALL waddamburo_text_context_render_song_title_rgba8(
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
    waddamburo_text_error *error)
{
    return waddamburo_text_context_render_song_title_ex_rgba8(
        context, utf8_title, utf8_subtitle, profile, 0U, outline_rgb, raster_scale,
        width, height, rgba8, rgba8_capacity, error);
}

waddamburo_text_result WADDAMBURO_TEXT_CALL waddamburo_text_context_render_song_title_ex_rgba8(
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
    waddamburo_text_error *error)
{
    const int squash = (flags & WADDAMBURO_TEXT_SONG_TITLE_SQUASH) != 0U;
    uint32_t base_width;
    uint32_t outline_radius;
    uint64_t pixel_count;
    uint8_t *mask = NULL;
    uint8_t *outline = NULL;
    waddamburo_text_result result;

    if (!error_is_valid(error) || context == NULL ||
        utf8_title == NULL || utf8_title[0] == '\0' || rgba8 == NULL ||
        raster_scale == 0U || raster_scale > 4U || outline_rgb > UINT32_C(0x00ffffff) ||
        (profile != WADDAMBURO_TEXT_PROFILE_SONG_COMPACT &&
         profile != WADDAMBURO_TEXT_PROFILE_SONG_EXPANDED &&
         profile != WADDAMBURO_TEXT_PROFILE_TRANSITION &&
         profile != WADDAMBURO_TEXT_PROFILE_GAMEPLAY_TITLE))
        return set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "Invalid song-title rasterizer arguments.");

    base_width = profile == WADDAMBURO_TEXT_PROFILE_SONG_COMPACT ? 56U
        : profile == WADDAMBURO_TEXT_PROFILE_SONG_EXPANDED ? 96U : 720U;
    if (width != base_width * raster_scale ||
        height != (profile == WADDAMBURO_TEXT_PROFILE_TRANSITION ? 103U
            : profile == WADDAMBURO_TEXT_PROFILE_GAMEPLAY_TITLE ? 64U : 400U) * raster_scale)
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

    outline_radius = (uint32_t)((profile == WADDAMBURO_TEXT_PROFILE_GAMEPLAY_TITLE ? 5.5f
        : profile == WADDAMBURO_TEXT_PROFILE_TRANSITION ? 5.0f : 4.5f) * raster_scale + 0.5f);

    if (profile == WADDAMBURO_TEXT_PROFILE_GAMEPLAY_TITLE) {
        result = render_title_row(
            context->face, utf8_title, raster_scale,
            mask, width, height, 1, 44.0f, 32.0f, error);
        FT_Set_Transform(context->face, NULL, NULL); /* the face is shared; drop any squeeze */
    } else if (profile == WADDAMBURO_TEXT_PROFILE_TRANSITION) {
        /* The artist line is smaller than the title after Latin and Japanese glyph metrics are
         * taken into account. Keep the two rows independently sized in the 103 px slot. */
        if (utf8_subtitle != NULL && utf8_subtitle[0] != '\0') {
            result = render_title_row(
                context->face, utf8_title, raster_scale,
                mask, width, height, 0, 46.0f, 36.0f, error);
            if (result == WADDAMBURO_TEXT_OK)
                result = render_title_row(
                    context->face, utf8_subtitle, raster_scale,
                    mask, width, height, 0, 22.0f, 84.0f, error);
        } else {
            result = render_title_row(
                context->face, utf8_title, raster_scale,
                mask, width, height, 0, 49.0f, 51.5f, error);
        }
    } else if (profile == WADDAMBURO_TEXT_PROFILE_SONG_COMPACT) {
        result = render_title_column(
            context->face, utf8_title, 38.0f * raster_scale, 35.0f * raster_scale,
            28.0f * raster_scale, 5U * raster_scale, height - 5U * raster_scale, squash, 0,
            mask, width, height, error);
    } else {
        result = render_title_column(
            context->face, utf8_title, 38.0f * raster_scale, 35.0f * raster_scale,
            70.0f * raster_scale, 5U * raster_scale, height - 5U * raster_scale, squash, 0,
            mask, width, height, error);
        /* The narrower artist column runs from the bottom, as in the arcade. */
        if (result == WADDAMBURO_TEXT_OK && utf8_subtitle != NULL && utf8_subtitle[0] != '\0') {
            result = render_title_column(
                context->face, utf8_subtitle, 21.0f * raster_scale, 21.0f * raster_scale,
                23.0f * raster_scale, 5U * raster_scale, height - 5U * raster_scale, squash, 1,
                mask, width, height, error);
        }
    }
    if (result != WADDAMBURO_TEXT_OK)
        goto song_failure;

    if (!dilate_mask_round(mask, outline, width, height, outline_radius)) {
        result = set_error(error, WADDAMBURO_TEXT_ERROR_MEMORY, 0, "Unable to outline the song-title surface.");
        goto song_failure;
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

    free(outline);
    free(mask);
    return set_error(error, WADDAMBURO_TEXT_OK, 0, "");

song_failure:
    free(outline);
    free(mask);
    return result;
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
    waddamburo_text_context *context = waddamburo_text_context_create(utf8_font_path, error);
    waddamburo_text_result result;
    if (context == NULL)
        return error_is_valid(error) ? error->code : WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT;
    result = waddamburo_text_context_render_song_title_rgba8(
        context, utf8_title, utf8_subtitle, profile, outline_rgb, raster_scale,
        width, height, rgba8, rgba8_capacity, error);
    waddamburo_text_context_destroy(context);
    return result;
}

waddamburo_text_result WADDAMBURO_TEXT_CALL waddamburo_text_context_render_glyph(
    waddamburo_text_context *context,
    uint32_t scalar,
    float font_px,
    uint32_t outline_radius,
    waddamburo_text_glyph *glyph,
    uint8_t *fill,
    uint8_t *outline,
    uint64_t mask_capacity,
    waddamburo_text_error *error)
{
    const FT_Bitmap *bitmap;
    FT_Error ft_error;
    uint64_t pixel_count;
    if (!error_is_valid(error))
        return WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT;
    if (context == NULL || glyph == NULL || glyph->struct_size < sizeof(*glyph) ||
        !(font_px >= 1.0f && font_px <= 1024.0f) || outline_radius > 256U)
        return set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "Invalid glyph rasterizer arguments.");
    /* The face is shared with the title profiles: its size and any squeeze are set per call. */
    FT_Set_Transform(context->face, NULL, NULL);
    ft_error = FT_Set_Pixel_Sizes(context->face, 0U, (FT_UInt)(font_px + 0.5f));
    if (ft_error == 0)
        ft_error = FT_Load_Char(context->face, scalar, FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL | FT_LOAD_RENDER);
    if (ft_error != 0)
        return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not render a glyph.");
    bitmap = &context->face->glyph->bitmap;
    glyph->width = bitmap->width + 2U * outline_radius;
    glyph->height = bitmap->rows + 2U * outline_radius;
    glyph->left = context->face->glyph->bitmap_left - (int32_t)outline_radius;
    glyph->top = context->face->glyph->bitmap_top + (int32_t)outline_radius;
    glyph->advance = (float)context->face->glyph->advance.x / 64.0f;
    glyph->ascender = (float)context->face->size->metrics.ascender / 64.0f;
    glyph->descender = (float)context->face->size->metrics.descender / 64.0f;
    pixel_count = (uint64_t)glyph->width * glyph->height;
    if (fill == NULL || outline == NULL || mask_capacity < pixel_count)
        return set_error(error, WADDAMBURO_TEXT_OK, 0, "");
    memset(fill, 0, (size_t)pixel_count);
    memset(outline, 0, (size_t)pixel_count);
    draw_bitmap_to_mask(fill, glyph->width, glyph->height, bitmap, (int)outline_radius, (int)outline_radius, 0);
    if (!dilate_mask_round(fill, outline, glyph->width, glyph->height, outline_radius))
        return set_error(error, WADDAMBURO_TEXT_ERROR_MEMORY, 0, "Unable to outline a glyph.");
    return set_error(error, WADDAMBURO_TEXT_OK, 0, "");
}
