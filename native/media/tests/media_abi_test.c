#include "waddamburo/media.h"

#include <stddef.h>
#include <string.h>

#define CHECK(condition)       \
    do {                       \
        if (!(condition))      \
            return __LINE__;   \
    } while (0)

_Static_assert(sizeof(void *) == 8U, "The initial media ABI targets 64-bit platforms");
_Static_assert(sizeof(waddamburo_media_decoder_options) == 16U, "Decoder options ABI changed");
_Static_assert(sizeof(waddamburo_media_stream_info) == 24U, "Stream info ABI changed");
_Static_assert(sizeof(waddamburo_media_error) == 272U, "Error ABI changed");
_Static_assert(sizeof(waddamburo_media_io_callbacks) == 40U, "I/O callback ABI changed");

static waddamburo_media_result WADDAMBURO_MEDIA_CALL read_callback(
    void *user_data,
    uint8_t *destination,
    uint64_t capacity,
    uint64_t *bytes_read)
{
    (void)user_data;
    (void)destination;
    (void)capacity;
    *bytes_read = 0U;
    return WADDAMBURO_MEDIA_END_OF_STREAM;
}

int main(void)
{
    uint32_t negotiated = 0U;
    waddamburo_media_decoder *decoder = NULL;
    waddamburo_media_decoder_options options = {sizeof(options), 0U, 0U, 0U};
    waddamburo_media_error error = {0};
    waddamburo_media_io_callbacks callbacks = {0};

    CHECK(waddamburo_media_get_abi_version() == WADDAMBURO_MEDIA_ABI_VERSION_1_0);
    CHECK(waddamburo_media_negotiate_abi(WADDAMBURO_MEDIA_ABI_VERSION_1_0, &negotiated) ==
          WADDAMBURO_MEDIA_OK);
    CHECK(negotiated == WADDAMBURO_MEDIA_ABI_VERSION_1_0);
    CHECK(waddamburo_media_negotiate_abi(WADDAMBURO_MEDIA_ABI_VERSION(2U, 0U), &negotiated) ==
          WADDAMBURO_MEDIA_ERROR_ABI_MISMATCH);
    CHECK(negotiated == 0U);
    CHECK(waddamburo_media_negotiate_abi(WADDAMBURO_MEDIA_ABI_VERSION(1U, 1U), &negotiated) ==
          WADDAMBURO_MEDIA_ERROR_ABI_MISMATCH);
    CHECK(negotiated == 0U);

    error.struct_size = sizeof(error);
    CHECK(waddamburo_media_decoder_create_file(&options, "synthetic.wav", &decoder, &error) ==
          WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE);
    CHECK(decoder == NULL);
    CHECK(error.code == WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE);
    CHECK(error.message_length == strlen(error.message));
    CHECK(strstr(error.message, "not built") != NULL);

    decoder = (waddamburo_media_decoder *)(uintptr_t)1U;
    error.struct_size = 0U;
    CHECK(waddamburo_media_decoder_create_file(&options, "synthetic.wav", &decoder, &error) ==
          WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT);
    CHECK(decoder == NULL);

    error.struct_size = sizeof(error);
    callbacks.struct_size = sizeof(callbacks);
    callbacks.read = read_callback;
    CHECK(waddamburo_media_decoder_create_callbacks(&options, &callbacks, &decoder, &error) ==
          WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE);
    CHECK(decoder == NULL);

    CHECK(waddamburo_media_decoder_cancel(NULL) == WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT);
    CHECK(waddamburo_media_decoder_seek(NULL, 0U) == WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT);
    waddamburo_media_decoder_destroy(NULL);
    return 0;
}
