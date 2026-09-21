#ifndef WADDAMBURO_MEDIA_H
#define WADDAMBURO_MEDIA_H

#include <stdint.h>

#if defined(_WIN32)
#define WADDAMBURO_MEDIA_CALL __cdecl
#if defined(WADDAMBURO_MEDIA_BUILDING_LIBRARY)
#define WADDAMBURO_MEDIA_API __declspec(dllexport)
#else
#define WADDAMBURO_MEDIA_API __declspec(dllimport)
#endif
#else
#define WADDAMBURO_MEDIA_CALL
#define WADDAMBURO_MEDIA_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define WADDAMBURO_MEDIA_ABI_VERSION(major, minor) \
    ((((uint32_t)(major)) << 16U) | ((uint32_t)(minor)))
#define WADDAMBURO_MEDIA_ABI_VERSION_MAJOR(version) ((uint32_t)(version) >> 16U)
#define WADDAMBURO_MEDIA_ABI_VERSION_MINOR(version) ((uint32_t)(version) & 0xffffU)
#define WADDAMBURO_MEDIA_ABI_VERSION_1_0 WADDAMBURO_MEDIA_ABI_VERSION(1U, 0U)
#define WADDAMBURO_MEDIA_ABI_VERSION_1_1 WADDAMBURO_MEDIA_ABI_VERSION(1U, 1U)
#define WADDAMBURO_MEDIA_ABI_VERSION_CURRENT WADDAMBURO_MEDIA_ABI_VERSION_1_1

typedef int32_t waddamburo_media_result;

#define WADDAMBURO_MEDIA_OK ((waddamburo_media_result)0)
#define WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT ((waddamburo_media_result)1)
#define WADDAMBURO_MEDIA_ERROR_ABI_MISMATCH ((waddamburo_media_result)2)
#define WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE ((waddamburo_media_result)3)
#define WADDAMBURO_MEDIA_ERROR_IO ((waddamburo_media_result)4)
#define WADDAMBURO_MEDIA_ERROR_UNSUPPORTED ((waddamburo_media_result)5)
#define WADDAMBURO_MEDIA_ERROR_CANCELLED ((waddamburo_media_result)6)
#define WADDAMBURO_MEDIA_END_OF_STREAM ((waddamburo_media_result)7)
#define WADDAMBURO_MEDIA_ERROR_INTERNAL ((waddamburo_media_result)8)

#define WADDAMBURO_MEDIA_SEEK_BEGIN ((uint32_t)0)
#define WADDAMBURO_MEDIA_SEEK_CURRENT ((uint32_t)1)
#define WADDAMBURO_MEDIA_SEEK_END ((uint32_t)2)
#define WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT UINT64_MAX

typedef struct waddamburo_media_decoder waddamburo_media_decoder;

typedef struct waddamburo_media_decoder_options {
    uint32_t struct_size;
    uint32_t output_sample_rate;
    uint32_t output_channels;
    uint32_t flags;
    /* Zero selects the default stream; positive values are one-based. */
    uint32_t source_stream_index;
    uint32_t reserved;
} waddamburo_media_decoder_options;

typedef struct waddamburo_media_stream_info {
    uint32_t struct_size;
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t reserved;
    uint64_t total_frames;
} waddamburo_media_stream_info;

typedef struct waddamburo_media_error {
    uint32_t struct_size;
    waddamburo_media_result code;
    int32_t native_code;
    uint32_t message_length;
    char message[256];
} waddamburo_media_error;

typedef waddamburo_media_result(WADDAMBURO_MEDIA_CALL *waddamburo_media_read_callback)(
    void *user_data,
    uint8_t *destination,
    uint64_t capacity,
    uint64_t *bytes_read);

typedef waddamburo_media_result(WADDAMBURO_MEDIA_CALL *waddamburo_media_seek_callback)(
    void *user_data,
    int64_t offset,
    uint32_t origin,
    uint64_t *position);

typedef int32_t(WADDAMBURO_MEDIA_CALL *waddamburo_media_cancel_callback)(void *user_data);

typedef struct waddamburo_media_io_callbacks {
    uint32_t struct_size;
    void *user_data;
    waddamburo_media_read_callback read;
    waddamburo_media_seek_callback seek;
    waddamburo_media_cancel_callback should_cancel;
} waddamburo_media_io_callbacks;

WADDAMBURO_MEDIA_API uint32_t WADDAMBURO_MEDIA_CALL waddamburo_media_get_abi_version(void);

WADDAMBURO_MEDIA_API waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_negotiate_abi(uint32_t requested_version, uint32_t *negotiated_version);

WADDAMBURO_MEDIA_API waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_create_file(
    const waddamburo_media_decoder_options *options,
    const char *utf8_path,
    waddamburo_media_decoder **decoder,
    waddamburo_media_error *error);

WADDAMBURO_MEDIA_API waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_create_callbacks(
    const waddamburo_media_decoder_options *options,
    const waddamburo_media_io_callbacks *callbacks,
    waddamburo_media_decoder **decoder,
    waddamburo_media_error *error);

WADDAMBURO_MEDIA_API waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_get_stream_info(
    waddamburo_media_decoder *decoder,
    waddamburo_media_stream_info *stream_info);

WADDAMBURO_MEDIA_API waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_read_frames(
    waddamburo_media_decoder *decoder,
    float *interleaved_samples,
    uint64_t frame_capacity,
    uint64_t *frames_read);

WADDAMBURO_MEDIA_API waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_seek(waddamburo_media_decoder *decoder, uint64_t frame_index);

WADDAMBURO_MEDIA_API waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_cancel(waddamburo_media_decoder *decoder);

WADDAMBURO_MEDIA_API waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_get_error(
    const waddamburo_media_decoder *decoder,
    waddamburo_media_error *error);

WADDAMBURO_MEDIA_API void WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_destroy(waddamburo_media_decoder *decoder);

#ifdef __cplusplus
}
#endif

#endif
