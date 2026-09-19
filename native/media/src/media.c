#include "waddamburo/media.h"

#include <stddef.h>
#include <string.h>

static int options_are_valid(const waddamburo_media_decoder_options *options)
{
    return options != NULL && options->struct_size >= sizeof(*options);
}

static int error_is_valid(const waddamburo_media_error *error)
{
    return error != NULL && error->struct_size >= sizeof(*error);
}

static void set_error(
    waddamburo_media_error *error,
    waddamburo_media_result code,
    const char *message)
{
    size_t message_length;

    if (!error_is_valid(error))
        return;

    message_length = strlen(message);
    if (message_length >= sizeof(error->message))
        message_length = sizeof(error->message) - 1U;

    error->code = code;
    error->native_code = 0;
    error->message_length = (uint32_t)message_length;
    memcpy(error->message, message, message_length);
    error->message[message_length] = '\0';
}

uint32_t WADDAMBURO_MEDIA_CALL waddamburo_media_get_abi_version(void)
{
    return WADDAMBURO_MEDIA_ABI_VERSION_CURRENT;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_negotiate_abi(uint32_t requested_version, uint32_t *negotiated_version)
{
    if (negotiated_version == NULL)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;

    *negotiated_version = 0U;
    if (WADDAMBURO_MEDIA_ABI_VERSION_MAJOR(requested_version) !=
        WADDAMBURO_MEDIA_ABI_VERSION_MAJOR(WADDAMBURO_MEDIA_ABI_VERSION_CURRENT))
        return WADDAMBURO_MEDIA_ERROR_ABI_MISMATCH;

    if (WADDAMBURO_MEDIA_ABI_VERSION_MINOR(requested_version) >
        WADDAMBURO_MEDIA_ABI_VERSION_MINOR(WADDAMBURO_MEDIA_ABI_VERSION_CURRENT))
        return WADDAMBURO_MEDIA_ERROR_ABI_MISMATCH;

    *negotiated_version = requested_version;
    return WADDAMBURO_MEDIA_OK;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_create_file(
    const waddamburo_media_decoder_options *options,
    const char *utf8_path,
    waddamburo_media_decoder **decoder,
    waddamburo_media_error *error)
{
    if (decoder == NULL)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;

    *decoder = NULL;
    if (!error_is_valid(error))
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;

    if (!options_are_valid(options) || utf8_path == NULL || utf8_path[0] == '\0') {
        set_error(error, WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT, "Invalid decoder arguments.");
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    }

    set_error(error, WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE,
              "The FFmpeg decoder backend is not built.");
    return WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_create_callbacks(
    const waddamburo_media_decoder_options *options,
    const waddamburo_media_io_callbacks *callbacks,
    waddamburo_media_decoder **decoder,
    waddamburo_media_error *error)
{
    if (decoder == NULL)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;

    *decoder = NULL;
    if (!error_is_valid(error))
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;

    if (!options_are_valid(options) || callbacks == NULL ||
        callbacks->struct_size < sizeof(*callbacks) || callbacks->read == NULL) {
        set_error(error, WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT, "Invalid decoder callbacks.");
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    }

    set_error(error, WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE,
              "The FFmpeg decoder backend is not built.");
    return WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_get_stream_info(
    waddamburo_media_decoder *decoder,
    waddamburo_media_stream_info *stream_info)
{
    if (decoder == NULL || stream_info == NULL || stream_info->struct_size < sizeof(*stream_info))
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    return WADDAMBURO_MEDIA_ERROR_INTERNAL;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_read_frames(
    waddamburo_media_decoder *decoder,
    float *interleaved_samples,
    uint64_t frame_capacity,
    uint64_t *frames_read)
{
    if (decoder == NULL || interleaved_samples == NULL || frame_capacity == 0U || frames_read == NULL)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    *frames_read = 0U;
    return WADDAMBURO_MEDIA_ERROR_INTERNAL;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_seek(waddamburo_media_decoder *decoder, uint64_t frame_index)
{
    (void)frame_index;
    return decoder == NULL ? WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT : WADDAMBURO_MEDIA_ERROR_INTERNAL;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_cancel(waddamburo_media_decoder *decoder)
{
    return decoder == NULL ? WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT : WADDAMBURO_MEDIA_ERROR_INTERNAL;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_get_error(
    const waddamburo_media_decoder *decoder,
    waddamburo_media_error *error)
{
    if (decoder == NULL || !error_is_valid(error))
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    return WADDAMBURO_MEDIA_ERROR_INTERNAL;
}

void WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_destroy(waddamburo_media_decoder *decoder)
{
    (void)decoder;
}
