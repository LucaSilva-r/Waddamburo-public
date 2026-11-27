#include "waddamburo/text.h"

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_GLYPH_H

#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

/* A chain of faces (ABI 1.7): the first is the font; each later one only draws the scalars the
 * faces before it lack (a title's rare symbols, Chinese or Hangul). */
#define WADDAMBURO_TEXT_MAX_FACES 4U

struct waddamburo_text_context {
    FT_Library library;
    FT_Face faces[WADDAMBURO_TEXT_MAX_FACES];
    uint32_t face_count;
};

/* The first face that has the scalar; the font's own (its missing-glyph box) when none has it. */
static FT_Face face_for(const waddamburo_text_context *context, uint32_t scalar)
{
    for (uint32_t i = 0U; i < context->face_count; ++i) {
        if (FT_Get_Char_Index(context->faces[i], scalar) != 0U)
            return context->faces[i];
    }
    return context->faces[0];
}

static FT_Error size_faces(waddamburo_text_context *context, FT_UInt pixels)
{
    for (uint32_t i = 0U; i < context->face_count; ++i) {
        FT_Error ft_error = FT_Set_Pixel_Sizes(context->faces[i], 0U, pixels);
        if (ft_error != 0)
            return ft_error;
    }
    return 0;
}

static void transform_faces(waddamburo_text_context *context, FT_Matrix *matrix)
{
    for (uint32_t i = 0U; i < context->face_count; ++i)
        FT_Set_Transform(context->faces[i], matrix, NULL);
}

/* The share of the em above the baseline, from the font's line box scaled to one em: fonts with a
 * taller box (extra line spacing, common in web fonts) would otherwise sit low in the title slots. */
static float em_ascent(FT_Face face)
{
    float box = (float)(face->ascender - face->descender);
    return box > 0.0f ? (float)face->ascender / box : 0.88f;
}

/* The line's ascender and descender in whole pixels at the size the faces were set to, rounded out
 * as FreeType rounds its own (a font whose box is one em lays out exactly as with those). */
static void line_metrics(FT_Face face, float font_px, float *ascender, float *descender)
{
    float pixels = (float)(FT_UInt)(font_px + 0.5f);
    *ascender = ceilf(pixels * em_ascent(face));
    *descender = floorf(pixels * (em_ascent(face) - 1.0f));
}

/* Loads the scalar from the face that has it; *face is that face (its glyph slot holds the result). */
static FT_Error load_char(waddamburo_text_context *context, uint32_t scalar, FT_Int32 flags, FT_Face *face)
{
    *face = face_for(context, scalar);
    return FT_Load_Char(*face, scalar, flags);
}

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
    waddamburo_text_context *context,
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
    FT_Face face;
    float ascent;
    float descent;
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
    ft_error = size_faces(context, (FT_UInt)(font_px + 0.5f));
    if (ft_error != 0)
        return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not size the song-title font.");
    line_metrics(context->faces[0], font_px, &ascent, &descent);
    ascent *= vertical;

    for (uint32_t i = 0U; i < item_count; ++i) {
        title_item item = items[i];
        float group_width = 0.0f;
        float x;
        for (uint32_t k = 0U; k < item.count; ++k) {
            ft_error = load_char(context, scalars[item.first + k], FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL, &face);
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
                transform_faces(context, &squash_matrix);
            }
            ft_error = load_char(context, scalar, FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL, &face);
            if (vertical < 1.0f)
                transform_faces(context, NULL);
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
    waddamburo_text_context *context,
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
    float line_ascender;
    float line_descender;
    FT_Error ft_error;
    FT_Face face;

    while (*cursor != 0U) {
        if (scalar_count == 256U)
            return set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Transition title exceeds 256 Unicode scalars.");
        if (!decode_utf8(&cursor, &scalars[scalar_count]))
            return set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Transition title is not valid UTF-8.");
        scalar_count++;
    }
    if (scalar_count == 0U)
        return set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Transition title is empty.");

    ft_error = size_faces(context, (FT_UInt)(font_px + 0.5f));
    if (ft_error != 0)
        return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not size the transition-title font.");
    for (uint32_t i = 0U; i < scalar_count; ++i) {
        ft_error = load_char(context, scalars[i], FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL, &face);
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
        transform_faces(context, &squeeze); /* advances below are transformed too */
        advance = 0.0f;
        for (uint32_t i = 0U; i < scalar_count; ++i) {
            ft_error = load_char(context, scalars[i], FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL, &face);
            if (ft_error != 0)
                return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not measure a gameplay-title glyph.");
            advance += (float)face->glyph->advance.x / 64.0f;
        }
    } else if (!gameplay && advance > width * 0.94f) {
        font_px *= width * 0.94f / advance;
        if (font_px < 6.0f)
            font_px = 6.0f;
        ft_error = size_faces(context, (FT_UInt)(font_px + 0.5f));
        if (ft_error != 0)
            return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not fit the transition-title font.");
        advance = 0.0f;
        for (uint32_t i = 0U; i < scalar_count; ++i) {
            ft_error = load_char(context, scalars[i], FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL, &face);
            if (ft_error != 0)
                return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not measure a transition-title glyph.");
            advance += (float)face->glyph->advance.x / 64.0f;
        }
    }

    line_metrics(context->faces[0], font_px, &line_ascender, &line_descender);
    pen_x = gameplay ? (float)width - margin - advance : ((float)width - advance) * 0.5f;
    (void)height;
    baseline = (int)(center_y * raster_scale
        + (line_ascender + line_descender) * 0.5f);
    for (uint32_t i = 0U; i < scalar_count; ++i) {
        FT_Glyph fill_glyph = NULL;
        FT_BitmapGlyph fill_bitmap;
        float glyph_advance;

        ft_error = load_char(context, scalars[i], FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL, &face);
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
    return WADDAMBURO_TEXT_ABI_VERSION_1_8;
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
    /* The path is a newline-separated chain: the font, then its fallbacks (ABI 1.7). */
    for (const char *start = utf8_font_path; ft_error == 0 && *start != '\0' && context->face_count < WADDAMBURO_TEXT_MAX_FACES;) {
        const char *end = strchr(start, '\n');
        size_t length = end != NULL ? (size_t)(end - start) : strlen(start);
        char *path = (char *)malloc(length + 1U);
        if (path == NULL) {
            ft_error = FT_Err_Out_Of_Memory;
            break;
        }
        memcpy(path, start, length);
        path[length] = '\0';
        if (length > 0U)
            ft_error = FT_New_Face(context->library, path, 0, &context->faces[context->face_count++]);
        free(path);
        if (ft_error != 0)
            context->face_count--;
        start = end != NULL ? end + 1 : start + length;
    }
    if (ft_error == 0 && context->face_count == 0U)
        ft_error = FT_Err_Cannot_Open_Resource;
    if (ft_error != 0) {
        waddamburo_text_context_destroy(context);
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
    for (uint32_t i = 0U; i < context->face_count; ++i)
        FT_Done_Face(context->faces[i]);
    if (context->library != NULL)
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
    waddamburo_text_context *context = NULL;
    waddamburo_text_error context_error;
    FT_Face face = NULL;
    FT_Error ft_error = 0;
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

    memset(&context_error, 0, sizeof(context_error));
    context_error.struct_size = sizeof(context_error);
    context = waddamburo_text_context_create(utf8_font_path, &context_error);
    if (context == NULL) {
        ft_error = context_error.native_code;
        goto font_failure;
    }
    pixel_size = (uint32_t)(((uint64_t)height * 9U) / ((uint64_t)glyph_count * 10U));
    if (pixel_size > width * 3U / 4U)
        pixel_size = width * 3U / 4U;
    if (pixel_size < 6U)
        pixel_size = 6U;
    ft_error = size_faces(context, pixel_size);
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
        ft_error = load_char(context, scalar, FT_LOAD_RENDER | FT_LOAD_TARGET_NORMAL, &face);
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

    waddamburo_text_context_destroy(context);
    free(outline);
    free(mask);
    return set_error(error, WADDAMBURO_TEXT_OK, 0, "");

font_failure:
    waddamburo_text_context_destroy(context);
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
            context, utf8_title, raster_scale,
            mask, width, height, 1, 44.0f, 32.0f, error);
        transform_faces(context, NULL); /* the faces are shared; drop any squeeze */
    } else if (profile == WADDAMBURO_TEXT_PROFILE_TRANSITION) {
        /* The artist line is smaller than the title after Latin and Japanese glyph metrics are
         * taken into account. Keep the two rows independently sized in the 103 px slot. */
        if (utf8_subtitle != NULL && utf8_subtitle[0] != '\0') {
            result = render_title_row(
                context, utf8_title, raster_scale,
                mask, width, height, 0, 46.0f, 36.0f, error);
            if (result == WADDAMBURO_TEXT_OK)
                result = render_title_row(
                    context, utf8_subtitle, raster_scale,
                    mask, width, height, 0, 22.0f, 84.0f, error);
        } else {
            result = render_title_row(
                context, utf8_title, raster_scale,
                mask, width, height, 0, 49.0f, 51.5f, error);
        }
    } else if (profile == WADDAMBURO_TEXT_PROFILE_SONG_COMPACT) {
        result = render_title_column(
            context, utf8_title, 38.0f * raster_scale, 35.0f * raster_scale,
            28.0f * raster_scale, 5U * raster_scale, height - 5U * raster_scale, squash, 0,
            mask, width, height, error);
    } else {
        result = render_title_column(
            context, utf8_title, 38.0f * raster_scale, 35.0f * raster_scale,
            70.0f * raster_scale, 5U * raster_scale, height - 5U * raster_scale, squash, 0,
            mask, width, height, error);
        /* The narrower artist column runs from the bottom, as in the arcade. */
        if (result == WADDAMBURO_TEXT_OK && utf8_subtitle != NULL && utf8_subtitle[0] != '\0') {
            result = render_title_column(
                context, utf8_subtitle, 21.0f * raster_scale, 21.0f * raster_scale,
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
    FT_Face face;
    uint64_t pixel_count;
    if (!error_is_valid(error))
        return WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT;
    if (context == NULL || glyph == NULL || glyph->struct_size < sizeof(*glyph) ||
        !(font_px >= 1.0f && font_px <= 1024.0f) || outline_radius > 256U)
        return set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "Invalid glyph rasterizer arguments.");
    /* The face is shared with the title profiles: its size and any squeeze are set per call. */
    transform_faces(context, NULL);
    ft_error = size_faces(context, (FT_UInt)(font_px + 0.5f));
    if (ft_error == 0)
        ft_error = load_char(context, scalar, FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL | FT_LOAD_RENDER, &face);
    if (ft_error != 0)
        return set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not render a glyph.");
    bitmap = &face->glyph->bitmap;
    glyph->width = bitmap->width + 2U * outline_radius;
    glyph->height = bitmap->rows + 2U * outline_radius;
    glyph->left = face->glyph->bitmap_left - (int32_t)outline_radius;
    glyph->top = face->glyph->bitmap_top + (int32_t)outline_radius;
    glyph->advance = (float)face->glyph->advance.x / 64.0f;
    /* The font's line metrics, whichever face drew the glyph: a fallback glyph sits on the same line. */
    line_metrics(context->faces[0], font_px, &glyph->ascender, &glyph->descender);
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

/* ---- Styled text (ABI 1.8) ----
 * Layout: the text is cut into lines (columns when vertical) along the main axis, each glyph placed
 * at `x` along its line. Fit: the layout is redone at smaller sizes, or squeezed along the line, until
 * it fits the box less the layers' reach. Drawing: one coverage mask of all the glyphs; one distance
 * field from it serves every layer's growth; each layer is then grown, moved, blurred and painted
 * over the surface, back to front. */

#define STYLED_MAX_SCALARS 1024U
#define STYLED_MAX_LAYERS 16U
#define STYLED_FAR 1e30f

typedef struct styled_glyph {
    uint32_t scalar;
    float advance;  /* horizontal: pen advance at the layout's size */
    float kern;     /* horizontal: kerning with the glyph before */
    float x;        /* along its line, from the line's start */
    uint32_t line;
    uint8_t hidden; /* a newline, or the space a wrapped line ended at */
    uint8_t rotate; /* vertical: drawn turned clockwise */
    uint8_t group;  /* vertical: drawn beside the glyph before (a !? pair) */
} styled_glyph;

static int styled_is_cjk(uint32_t s)
{
    return (s >= 0x2e80U && s <= 0x9fffU) || (s >= 0xac00U && s <= 0xd7afU) ||
        (s >= 0xf900U && s <= 0xfaffU) || (s >= 0xff00U && s <= 0xffefU) || s >= 0x20000U;
}

/* Characters a line may not start with (closing marks, small kana, the long vowel). */
static int styled_no_line_start(uint32_t s)
{
    static const uint32_t values[] = {
        ',', '.', '!', '?', ')', ']', '}', ':', ';', 0x3001U, 0x3002U, 0xff0cU, 0xff0eU, 0xff01U, 0xff1fU,
        0x30fcU, 0x30fbU, 0x3063U, 0x3083U, 0x3085U, 0x3087U, 0x3041U, 0x3043U, 0x3045U, 0x3047U, 0x3049U,
        0x30c3U, 0x30e3U, 0x30e5U, 0x30e7U, 0x30a1U, 0x30a3U, 0x30a5U, 0x30a7U, 0x30a9U,
        0xff09U, 0x300dU, 0x300fU, 0x3011U,
    };
    return scalar_in_list(s, values, sizeof(values) / sizeof(values[0]));
}

/* How far glyph i starts after the one before it on its line. */
static float styled_step(const styled_glyph *glyphs, uint32_t i, int vertical, float font_px, float spacing)
{
    if (vertical) {
        uint32_t s = glyphs[i].scalar;
        float lead = font_px + spacing;
        if (glyphs[i].group)
            return 0.0f;
        return is_beside_scalar(s) ? lead * 0.3f : has_reduced_leading(s) ? lead * 0.8f : lead;
    }
    return glyphs[i - 1U].advance + glyphs[i].kern + spacing;
}

static float styled_own(const styled_glyph *glyph, int vertical, float font_px)
{
    return vertical ? font_px : glyph->advance;
}

/* Measures the glyphs at font_px and cuts them into lines no longer than `limit` along the line.
 * line_length[l] is each line's length. Returns the line count, 0 on a FreeType failure. */
static uint32_t styled_layout(
    waddamburo_text_context *context, styled_glyph *glyphs, uint32_t count, int vertical,
    float font_px, float spacing, float limit, float *line_length, FT_Error *ft_error)
{
    uint32_t line = 0U, start = 0U;
    int last_break = -1;
    float x = 0.0f;
    FT_Face previous_face = NULL;
    FT_UInt previous_index = 0U;
    *ft_error = size_faces(context, (FT_UInt)(font_px + 0.5f));
    if (*ft_error != 0)
        return 0U;
    for (uint32_t i = 0U; i < count; ++i) {
        FT_Face face;
        FT_UInt index;
        glyphs[i].hidden = glyphs[i].scalar == '\n';
        glyphs[i].kern = 0.0f;
        glyphs[i].advance = 0.0f;
        if (glyphs[i].hidden) {
            previous_face = NULL;
            continue;
        }
        *ft_error = load_char(context, glyphs[i].scalar, FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL, &face);
        if (*ft_error != 0)
            return 0U;
        glyphs[i].advance = (float)face->glyph->advance.x / 64.0f;
        index = FT_Get_Char_Index(face, glyphs[i].scalar);
        if (!vertical && previous_face == face && FT_HAS_KERNING(face)) {
            FT_Vector delta;
            if (FT_Get_Kerning(face, previous_index, index, FT_KERNING_DEFAULT, &delta) == 0)
                glyphs[i].kern = (float)delta.x / 64.0f;
        }
        previous_face = face;
        previous_index = index;
    }
    for (uint32_t i = 0U; i <= count; ++i)
        line_length[i] = 0.0f;
    for (uint32_t i = 0U; i < count; ++i) {
        styled_glyph *g = &glyphs[i];
        if (g->scalar == '\n') {
            g->line = line;
            line++;
            start = i + 1U;
            last_break = -1;
            continue;
        }
        g->hidden = 0U;
        g->x = i == start ? 0.0f : x + styled_step(glyphs, i, vertical, font_px, spacing);
        /* Too long: a new line at the last place one may start, else before this glyph. */
        if (i > start && g->x + styled_own(g, vertical, font_px) > limit && !g->group) {
            uint32_t next = last_break > (int)start ? (uint32_t)last_break : i;
            if (glyphs[next - 1U].scalar == ' ')
                glyphs[next - 1U].hidden = 1U;
            line++;
            start = next;
            last_break = -1;
            i = next - 1U; /* the loop's ++ starts the new line at `next` */
            continue;
        }
        g->line = line;
        x = g->x;
        if (!glyphs[i].hidden) {
            float end = g->x + styled_own(g, vertical, font_px);
            if (end > line_length[line])
                line_length[line] = end;
        }
        /* Where a line may start: after a space, or around a CJK character (not before a closing mark). */
        if (i + 1U < count && !glyphs[i + 1U].group && !styled_no_line_start(glyphs[i + 1U].scalar) &&
            (g->scalar == ' ' || styled_is_cjk(g->scalar) || styled_is_cjk(glyphs[i + 1U].scalar)))
            last_break = (int)(i + 1U);
    }
    /* A space that ended a line is not part of it. */
    for (uint32_t l = 0U; l <= line; ++l)
        line_length[l] = 0.0f;
    for (uint32_t i = 0U; i < count; ++i)
        if (!glyphs[i].hidden && glyphs[i].scalar != '\n') {
            float end = glyphs[i].x + styled_own(&glyphs[i], vertical, font_px);
            if (end > line_length[glyphs[i].line])
                line_length[glyphs[i].line] = end;
        }
    return line + 1U;
}

/* Squared-distance field of the ink (fill at half coverage or more), as its square root, within
 * `reach` of the ink's box; STYLED_FAR elsewhere. */
static int styled_distance(const uint8_t *fill, float *distance, uint32_t width, uint32_t height, uint32_t reach)
{
    uint32_t ink_left = width, ink_right = 0U, ink_top = height, ink_bottom = 0U;
    uint32_t box_left, box_top, box_width, box_height, longest;
    float *grid, *line, *result, *z;
    uint32_t *v;
    for (size_t i = 0U; i < (size_t)width * height; ++i)
        distance[i] = STYLED_FAR;
    for (uint32_t y = 0U; y < height; ++y)
        for (uint32_t x = 0U; x < width; ++x)
            if (fill[(size_t)y * width + x] >= 128U) {
                if (x < ink_left) ink_left = x;
                if (x > ink_right) ink_right = x;
                if (y < ink_top) ink_top = y;
                ink_bottom = y;
            }
    if (ink_left > ink_right)
        return 1;
    box_left = ink_left > reach ? ink_left - reach : 0U;
    box_top = ink_top > reach ? ink_top - reach : 0U;
    box_width = (ink_right + reach < width ? ink_right + reach : width - 1U) - box_left + 1U;
    box_height = (ink_bottom + reach < height ? ink_bottom + reach : height - 1U) - box_top + 1U;
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
            grid[(size_t)y * box_width + x] = fill[(size_t)(box_top + y) * width + box_left + x] >= 128U ? 0.0f : 1e20f;
    for (uint32_t x = 0U; x < box_width; ++x) {
        for (uint32_t y = 0U; y < box_height; ++y)
            line[y] = grid[(size_t)y * box_width + x];
        distance_1d(line, result, box_height, v, z);
        for (uint32_t y = 0U; y < box_height; ++y)
            grid[(size_t)y * box_width + x] = result[y];
    }
    for (uint32_t y = 0U; y < box_height; ++y) {
        distance_1d(grid + (size_t)y * box_width, result, box_width, v, z);
        for (uint32_t x = 0U; x < box_width; ++x)
            distance[(size_t)(box_top + y) * width + box_left + x] = sqrtf(result[x]);
    }
    free(grid); free(line); free(result); free(z); free(v);
    return 1;
}

/* One box-blur pass along rows (step 1) or columns (step width) of radius r; zero outside. */
static void styled_box_pass(const float *source, float *target, uint32_t width, uint32_t height, int rows, int r)
{
    uint32_t lines = rows ? height : width, length = rows ? width : height;
    size_t stride = rows ? 1U : width, next = rows ? width : 1U;
    float scale = 1.0f / (float)(2 * r + 1);
    for (uint32_t l = 0U; l < lines; ++l) {
        const float *in = source + (size_t)l * next;
        float *out = target + (size_t)l * next;
        float sum = 0.0f;
        for (int k = 0; k <= r && k < (int)length; ++k)
            sum += in[(size_t)k * stride];
        for (uint32_t p = 0U; p < length; ++p) {
            out[(size_t)p * stride] = sum * scale;
            if ((int)p + r + 1 < (int)length)
                sum += in[(size_t)(p + (uint32_t)r + 1U) * stride];
            if ((int)p - r >= 0)
                sum -= in[(size_t)(p - (uint32_t)r) * stride];
        }
    }
}

static float styled_channel(uint32_t rgba, int shift)
{
    return (float)((rgba >> shift) & 255U) / 255.0f;
}

/* Renders one glyph's coverage into the mask with its pen at (pen_x, baseline) under `matrix`
 * (NULL: none); turned clockwise with its box's top-left at (pen_x, baseline) when `rotate`. */
static FT_Error styled_draw_glyph(
    waddamburo_text_context *context, uint32_t scalar, FT_Matrix *matrix, float pen_x, float baseline,
    int rotate, uint8_t *mask, uint32_t width, uint32_t height, FT_Face *face_out, uint32_t *rows, uint32_t *columns)
{
    FT_Face face;
    FT_Error ft_error;
    transform_faces(context, matrix);
    ft_error = load_char(context, scalar, FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL | FT_LOAD_RENDER, &face);
    transform_faces(context, NULL);
    if (ft_error != 0)
        return ft_error;
    if (rows != NULL) *rows = face->glyph->bitmap.rows;
    if (columns != NULL) *columns = face->glyph->bitmap.width;
    if (face_out != NULL) *face_out = face;
    if (rotate)
        draw_bitmap_to_mask(mask, width, height, &face->glyph->bitmap, (int)floorf(pen_x + 0.5f), (int)floorf(baseline + 0.5f), 1);
    else
        draw_bitmap_to_mask(mask, width, height, &face->glyph->bitmap,
            (int)floorf(pen_x + 0.5f) + face->glyph->bitmap_left, (int)floorf(baseline + 0.5f) - face->glyph->bitmap_top, 0);
    return 0;
}

waddamburo_text_result WADDAMBURO_TEXT_CALL waddamburo_text_context_render_styled_rgba8(
    waddamburo_text_context *context,
    const char *utf8_text,
    const waddamburo_text_style *style,
    float scale,
    uint32_t width,
    uint32_t height,
    uint8_t *rgba8,
    uint64_t rgba8_capacity,
    waddamburo_text_error *error)
{
    waddamburo_text_result result = WADDAMBURO_TEXT_OK;
    styled_glyph *glyphs = NULL;
    float *line_length = NULL, *band_top = NULL, *band_bottom = NULL, *row_t = NULL;
    float *distance = NULL, *cover = NULL, *scratch = NULL;
    uint8_t *mask = NULL, *ink = NULL;
    uint32_t count = 0U, lines = 0U, bands = 0U;
    uint32_t region_left = 0U, region_top = 0U, region_width = 0U, region_height = 0U;
    size_t region_count;
    uint64_t pixel_count;
    const uint8_t *cursor;
    int vertical;
    float font_px, spacing, line_factor, reach = 0.0f, pad, avail_w, avail_h, squeeze = 1.0f;
    float ascender = 0.0f, descender = 0.0f, line_box, line_advance, main_length = 0.0f, cross_length = 0.0f;
    FT_Error ft_error = 0;
    int need_distance = 0;

    if (!error_is_valid(error))
        return WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT;
    if (context == NULL || utf8_text == NULL || style == NULL || style->struct_size < sizeof(*style) ||
        rgba8 == NULL || width == 0U || height == 0U || !(scale > 0.0f && scale <= 8.0f) ||
        !(style->font_px * scale >= 1.0f && style->font_px * scale <= 1024.0f) ||
        style->layer_count == 0U || style->layer_count > STYLED_MAX_LAYERS || style->layers == NULL ||
        style->align > 2U || style->vertical_align > 2U || style->fit > 3U)
        return set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "Invalid styled text arguments.");
    pixel_count = (uint64_t)width * height;
    if (pixel_count > (UINT64_C(1) << 26) || rgba8_capacity != pixel_count * 4U)
        return set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "RGBA8 buffer size does not match the styled text surface.");
    memset(rgba8, 0, (size_t)pixel_count * 4U);

    glyphs = (styled_glyph *)calloc(STYLED_MAX_SCALARS, sizeof(*glyphs));
    if (glyphs == NULL)
        return set_error(error, WADDAMBURO_TEXT_ERROR_MEMORY, 0, "Unable to allocate the styled text.");
    for (cursor = (const uint8_t *)utf8_text; *cursor != 0U;) {
        if (count == STYLED_MAX_SCALARS) {
            result = set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Text exceeds 1024 Unicode scalars.");
            goto done;
        }
        if (!decode_utf8(&cursor, &glyphs[count].scalar)) {
            result = set_error(error, WADDAMBURO_TEXT_ERROR_TEXT, 0, "Text is not valid UTF-8.");
            goto done;
        }
        if (glyphs[count].scalar != '\r')
            count++;
    }
    if (count == 0U)
        goto done;

    vertical = style->vertical != 0U;
    for (uint32_t i = 0U; i < count; ++i) {
        uint32_t s = glyphs[i].scalar;
        glyphs[i].rotate = (uint8_t)(vertical && !is_beside_scalar(s) && is_rotated_scalar(s));
        glyphs[i].group = (uint8_t)(vertical && i > 0U && is_horizontal_group_scalar(s) &&
            is_horizontal_group_scalar(glyphs[i - 1U].scalar) && !glyphs[i - 1U].group);
    }
    for (uint32_t l = 0U; l < style->layer_count; ++l) {
        const waddamburo_text_layer *layer = &style->layers[l];
        float r = (layer->grow > 0.0f ? layer->grow : 0.0f) + (layer->blur > 0.0f ? layer->blur : 0.0f);
        if (!(layer->grow >= 0.0f && layer->grow <= 256.0f && layer->blur >= 0.0f && layer->blur <= 256.0f)) {
            result = set_error(error, WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT, 0, "A text layer's grow or blur is out of range.");
            goto done;
        }
        if (r > reach) reach = r;
        if (layer->grow > 0.0f) need_distance = 1;
    }
    pad = reach * scale + 1.0f;
    avail_w = (float)width - 2.0f * pad;
    avail_h = (float)height - 2.0f * pad;
    if (avail_w < 1.0f) avail_w = 1.0f;
    if (avail_h < 1.0f) avail_h = 1.0f;
    line_length = (float *)calloc((size_t)count + 1U, sizeof(*line_length));
    if (line_length == NULL) {
        result = set_error(error, WADDAMBURO_TEXT_ERROR_MEMORY, 0, "Unable to allocate the styled text.");
        goto done;
    }

    /* Fit: lay out, measure, and size down (or squeeze along the line) until the text fits. */
    font_px = style->font_px * scale;
    line_factor = style->line_height > 0.0f ? style->line_height : 1.0f;
    for (int attempt = 0; attempt < 16; ++attempt) {
        float avail_main = vertical ? avail_h : avail_w, avail_cross = vertical ? avail_w : avail_h, fit;
        spacing = style->letter_spacing * scale * (font_px / (style->font_px * scale));
        lines = styled_layout(context, glyphs, count, vertical, font_px, spacing,
            style->fit == WADDAMBURO_TEXT_FIT_WRAP ? avail_main : STYLED_FAR, line_length, &ft_error);
        if (lines == 0U) {
            result = set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not lay out the text.");
            goto done;
        }
        line_metrics(context->faces[0], font_px, &ascender, &descender);
        line_box = vertical ? font_px : ascender - descender;
        line_advance = line_box * line_factor;
        main_length = 0.0f;
        for (uint32_t l = 0U; l < lines; ++l)
            if (line_length[l] > main_length) main_length = line_length[l];
        cross_length = (float)(lines - 1U) * line_advance + line_box;
        squeeze = 1.0f;
        if (style->fit == WADDAMBURO_TEXT_FIT_CLIP || font_px <= 4.0f)
            break;
        if (style->fit == WADDAMBURO_TEXT_FIT_SQUEEZE) {
            if (main_length > avail_main)
                squeeze = avail_main / main_length;
            if (cross_length <= avail_cross)
                break;
            font_px *= avail_cross / cross_length * 0.999f;
        } else {
            fit = avail_main / (main_length > 0.0f ? main_length : 1.0f);
            if (avail_cross / cross_length < fit)
                fit = avail_cross / cross_length;
            if (fit >= 1.0f)
                break;
            /* Wrapped text is as tall as the square of its size: shrink by about the root, and lay out again. */
            font_px *= style->fit == WADDAMBURO_TEXT_FIT_WRAP ? fminf(sqrtf(fit), 0.98f) : fit * 0.999f;
        }
        if (font_px < 4.0f)
            font_px = 4.0f;
    }

    mask = (uint8_t *)calloc((size_t)pixel_count, 1U);
    band_top = (float *)calloc((size_t)count + 1U, sizeof(*band_top));
    band_bottom = (float *)calloc((size_t)count + 1U, sizeof(*band_bottom));
    if (mask == NULL || band_top == NULL || band_bottom == NULL) {
        result = set_error(error, WADDAMBURO_TEXT_ERROR_MEMORY, 0, "Unable to allocate the styled text surface.");
        goto done;
    }
    ft_error = size_faces(context, (FT_UInt)(font_px + 0.5f));
    if (ft_error != 0) {
        result = set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not size the text.");
        goto done;
    }
    if (!vertical) {
        FT_Matrix matrix;
        float top = pad + (avail_h - cross_length) * 0.5f * (float)style->vertical_align;
        matrix.xx = (FT_Fixed)(squeeze * 65536.0f);
        matrix.xy = (FT_Fixed)(style->slant * 65536.0f);
        matrix.yx = 0;
        matrix.yy = 0x10000;
        for (uint32_t l = 0U; l < lines; ++l) {
            float baseline = top + (float)l * line_advance + ascender;
            band_top[bands] = baseline - ascender;
            band_bottom[bands++] = baseline - descender;
        }
        for (uint32_t i = 0U; i < count; ++i) {
            const styled_glyph *g = &glyphs[i];
            float line_start, baseline;
            if (g->hidden || g->scalar == '\n' || g->scalar == ' ')
                continue;
            line_start = pad + (avail_w - line_length[g->line] * squeeze) * 0.5f * (float)style->align;
            baseline = top + (float)g->line * line_advance + ascender;
            ft_error = styled_draw_glyph(context, g->scalar, &matrix, line_start + g->x * squeeze, baseline, 0,
                mask, width, height, NULL, NULL, NULL);
            if (ft_error != 0)
                break;
        }
    } else {
        /* Columns right to left; vertical_align places the block across them (start: the right). */
        float right = pad + avail_w - (avail_w - cross_length) * 0.5f * (float)style->vertical_align;
        float ascent = ascender * squeeze;
        FT_Matrix upright, turned;
        upright.xx = 0x10000; upright.xy = 0; upright.yx = 0; upright.yy = (FT_Fixed)(squeeze * 65536.0f);
        turned.xx = (FT_Fixed)(squeeze * 65536.0f); turned.xy = 0; turned.yx = 0; turned.yy = 0x10000;
        for (uint32_t i = 0U; i < count && ft_error == 0;) {
            const styled_glyph *g = &glyphs[i];
            float centre, cell_top, group_width = 0.0f, x;
            uint32_t members = 1U;
            if (g->hidden || g->scalar == '\n' || g->scalar == ' ' || g->group) {
                i++;
                continue;
            }
            while (i + members < count && glyphs[i + members].group)
                members++;
            centre = right - (float)g->line * line_advance - font_px * 0.5f;
            cell_top = pad + (avail_h - line_length[g->line] * squeeze) * 0.5f * (float)style->align + g->x * squeeze;
            band_top[bands] = cell_top;
            band_bottom[bands++] = cell_top + font_px * squeeze;
            if (g->rotate) {
                uint32_t rows = 0U, columns = 0U;
                FT_Face face;
                /* Measured first: the turned glyph is centred on the column. */
                transform_faces(context, &turned);
                ft_error = load_char(context, g->scalar, FT_LOAD_DEFAULT | FT_LOAD_TARGET_NORMAL | FT_LOAD_RENDER, &face);
                transform_faces(context, NULL);
                if (ft_error != 0)
                    break;
                rows = face->glyph->bitmap.rows;
                columns = face->glyph->bitmap.width;
                ft_error = styled_draw_glyph(context, g->scalar, &turned, centre - (float)rows * 0.5f,
                    cell_top + (font_px * squeeze - (float)columns) * 0.5f, 1, mask, width, height, NULL, NULL, NULL);
                i++;
                continue;
            }
            for (uint32_t k = 0U; k < members; ++k)
                group_width += glyphs[i + k].advance;
            x = centre - group_width * 0.5f;
            for (uint32_t k = 0U; k < members && ft_error == 0; ++k) {
                uint32_t s = glyphs[i + k].scalar;
                float baseline = cell_top + ascent + ((s == '\'' || s == '"') ? font_px * squeeze * 0.7f : 0.0f);
                ft_error = styled_draw_glyph(context, s, &upright, x, baseline, 0, mask, width, height, NULL, NULL, NULL);
                x += glyphs[i + k].advance;
            }
            i += members;
        }
    }
    if (ft_error != 0) {
        result = set_error(error, WADDAMBURO_TEXT_ERROR_FONT, ft_error, "FreeType could not render the text.");
        goto done;
    }

    /* The work is confined to the ink's box grown by the layers' reach and offsets (a score's box is
     * mostly empty; every loop below is per pixel). Coordinates below are the region's. */
    {
        uint32_t ink_left = width, ink_right = 0U, ink_top = height, ink_bottom = 0U, margin = (uint32_t)(reach * scale) + 2U;
        for (uint32_t l = 0U; l < style->layer_count; ++l) {
            uint32_t ox = (uint32_t)fabsf(style->layers[l].offset_x * scale) + 1U, oy = (uint32_t)fabsf(style->layers[l].offset_y * scale) + 1U;
            if (ox + (uint32_t)(reach * scale) + 2U > margin) margin = ox + (uint32_t)(reach * scale) + 2U;
            if (oy + (uint32_t)(reach * scale) + 2U > margin) margin = oy + (uint32_t)(reach * scale) + 2U;
        }
        for (uint32_t y = 0U; y < height; ++y)
            for (uint32_t x = 0U; x < width; ++x)
                if (mask[(size_t)y * width + x] != 0U) {
                    if (x < ink_left) ink_left = x;
                    if (x > ink_right) ink_right = x;
                    if (y < ink_top) ink_top = y;
                    ink_bottom = y;
                }
        if (ink_left > ink_right)
            goto done;
        region_left = ink_left > margin ? ink_left - margin : 0U;
        region_top = ink_top > margin ? ink_top - margin : 0U;
        region_width = (ink_right + margin < width ? ink_right + margin : width - 1U) - region_left + 1U;
        region_height = (ink_bottom + margin < height ? ink_bottom + margin : height - 1U) - region_top + 1U;
    }
    region_count = (size_t)region_width * region_height;
    ink = (uint8_t *)malloc(region_count);
    row_t = (float *)malloc((size_t)region_height * sizeof(*row_t));
    cover = (float *)malloc(region_count * sizeof(*cover));
    scratch = (float *)malloc(region_count * sizeof(*scratch));
    distance = need_distance ? (float *)malloc(region_count * sizeof(*distance)) : NULL;
    if (ink == NULL || row_t == NULL || cover == NULL || scratch == NULL || (need_distance && distance == NULL)) {
        result = set_error(error, WADDAMBURO_TEXT_ERROR_MEMORY, 0, "Unable to allocate the text layers.");
        goto done;
    }
    for (uint32_t y = 0U; y < region_height; ++y)
        memcpy(ink + (size_t)y * region_width, mask + (size_t)(region_top + y) * width + region_left, region_width);
    /* Each row's place in its line's gradient (the nearest line's band). */
    for (uint32_t y = 0U; y < region_height; ++y) {
        float row = (float)(region_top + y) + 0.5f, best = STYLED_FAR, t = 0.0f;
        for (uint32_t b = 0U; b < bands; ++b) {
            float middle = (band_top[b] + band_bottom[b]) * 0.5f, gap = fabsf(row - middle);
            if (gap < best) {
                float span = band_bottom[b] - band_top[b];
                best = gap;
                t = span > 0.0f ? (row - band_top[b]) / span : 0.0f;
            }
        }
        row_t[y] = t < 0.0f ? 0.0f : t > 1.0f ? 1.0f : t;
    }
    if (need_distance && !styled_distance(ink, distance, region_width, region_height, (uint32_t)(reach * scale) + 2U)) {
        result = set_error(error, WADDAMBURO_TEXT_ERROR_MEMORY, 0, "Unable to outline the text.");
        goto done;
    }

    for (uint32_t l = 0U; l < style->layer_count; ++l) {
        const waddamburo_text_layer *layer = &style->layers[l];
        float grow = layer->grow * scale, blur = layer->blur * scale;
        int offset_x = (int)floorf(layer->offset_x * scale + 0.5f), offset_y = (int)floorf(layer->offset_y * scale + 0.5f);
        int blurred = blur >= 0.5f;
        float top[4], bottom[4];
        for (int k = 0; k < 4; ++k) {
            top[k] = styled_channel(layer->top_rgba, 24 - 8 * k);
            bottom[k] = styled_channel(layer->bottom_rgba, 24 - 8 * k);
        }
        if (blurred) {
            /* Three box passes each way: close to a gaussian of about blur / 2. */
            int r = (int)(blur * 0.5f + 0.5f);
            for (size_t i = 0U; i < region_count; ++i) {
                float a = (float)ink[i] * (1.0f / 255.0f);
                if (grow > 0.0f) {
                    float border = grow + 0.5f - distance[i];
                    border = border < 0.0f ? 0.0f : border > 1.0f ? 1.0f : border;
                    if (border > a) a = border;
                }
                cover[i] = a;
            }
            for (int pass = 0; pass < 3; ++pass) {
                styled_box_pass(cover, scratch, region_width, region_height, 1, r);
                styled_box_pass(scratch, cover, region_width, region_height, 0, r);
            }
        }
        for (uint32_t y = 0U; y < region_height; ++y) {
            int source_y = (int)y - offset_y;
            float t, colour[4];
            int x_from, x_to;
            const uint8_t *ink_row;
            const float *cover_row, *distance_row;
            uint8_t *pixel;
            if (source_y < 0 || source_y >= (int)region_height)
                continue;
            t = row_t[source_y];
            for (int k = 0; k < 4; ++k)
                colour[k] = top[k] + (bottom[k] - top[k]) * t;
            /* Premultiplied, scaled to bytes. */
            colour[0] *= colour[3] * 255.0f;
            colour[1] *= colour[3] * 255.0f;
            colour[2] *= colour[3] * 255.0f;
            x_from = offset_x > 0 ? offset_x : 0;
            x_to = (int)region_width + (offset_x < 0 ? offset_x : 0);
            ink_row = ink + (size_t)source_y * region_width - offset_x;
            cover_row = cover + (size_t)source_y * region_width - offset_x;
            distance_row = need_distance ? distance + (size_t)source_y * region_width - offset_x : NULL;
            pixel = rgba8 + ((size_t)(region_top + y) * width + region_left + (uint32_t)x_from) * 4U;
            for (int x = x_from; x < x_to; ++x, pixel += 4) {
                float a;
                if (blurred)
                    a = cover_row[x];
                else {
                    a = (float)ink_row[x] * (1.0f / 255.0f);
                    if (grow > 0.0f) {
                        float border = grow + 0.5f - distance_row[x];
                        if (border > a) a = border > 1.0f ? 1.0f : border;
                    }
                }
                if (a <= 0.0f)
                    continue;
                {
                    float keep = 1.0f - a * colour[3];
                    /* Premultiplied "over". */
                    pixel[0] = (uint8_t)(colour[0] * a + (float)pixel[0] * keep + 0.5f);
                    pixel[1] = (uint8_t)(colour[1] * a + (float)pixel[1] * keep + 0.5f);
                    pixel[2] = (uint8_t)(colour[2] * a + (float)pixel[2] * keep + 0.5f);
                    pixel[3] = (uint8_t)(colour[3] * a * 255.0f + (float)pixel[3] * keep + 0.5f);
                }
            }
        }
    }

done:
    transform_faces(context, NULL);
    free(glyphs); free(line_length); free(band_top); free(band_bottom); free(row_t);
    free(distance); free(cover); free(scratch); free(mask); free(ink);
    if (result == WADDAMBURO_TEXT_OK)
        set_error(error, WADDAMBURO_TEXT_OK, 0, "");
    return result;
}
