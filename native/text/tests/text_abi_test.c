#include "waddamburo/text.h"

#include <stdint.h>
#include <stdio.h>

#define CHECK(condition) do { if (!(condition)) { fprintf(stderr, "check failed: %s\n", #condition); return 1; } } while (0)

int main(void)
{
    waddamburo_text_error error = {sizeof(error), 0, 0, 0, {0}};
    uint8_t pixel[4] = {0};
    waddamburo_text_layer layer = {0.0f, 0.0f, 0.0f, 0.0f, 0xffffffffU, 0xffffffffU, 0.0f, 0.0f, 0.0f, 0U, 0U};
    waddamburo_text_style style = {sizeof(style), 24.0f, 0.0f, 0.0f, 0.0f, 0U, 0U, 0U, 0U, 1U, &layer};
    CHECK(waddamburo_text_get_abi_version() == WADDAMBURO_TEXT_ABI_VERSION_1_10);
    CHECK(waddamburo_text_context_create(NULL, &error) == NULL);
    CHECK(error.code == WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT);
    CHECK(waddamburo_text_render_vertical_rgba8(NULL, "title", 1U, 1U, pixel, 4U, &error) ==
          WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT);
    CHECK(error.code == WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT);
    CHECK(waddamburo_text_render_song_title_rgba8(
              NULL, "title", NULL, WADDAMBURO_TEXT_PROFILE_SONG_COMPACT,
              0U, 1U, 56U, 400U, pixel, 4U, &error) ==
          WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT);
    CHECK(waddamburo_text_context_render_styled_rgba8(NULL, "text", &style, 1.0f, 1U, 1U, pixel, 4U, &error) ==
          WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT);
    return 0;
}
