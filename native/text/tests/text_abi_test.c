#include "waddamburo/text.h"

#include <stdint.h>
#include <stdio.h>

#define CHECK(condition) do { if (!(condition)) { fprintf(stderr, "check failed: %s\n", #condition); return 1; } } while (0)

int main(void)
{
    waddamburo_text_error error = {sizeof(error), 0, 0, 0, {0}};
    uint8_t pixel[4] = {0};
    CHECK(waddamburo_text_get_abi_version() == WADDAMBURO_TEXT_ABI_VERSION_1_0);
    CHECK(waddamburo_text_render_vertical_rgba8(NULL, "title", 1U, 1U, pixel, 4U, &error) ==
          WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT);
    CHECK(error.code == WADDAMBURO_TEXT_ERROR_INVALID_ARGUMENT);
    return 0;
}
