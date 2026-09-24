#include "waddamburo/media.h"

#include <stddef.h>
#include <stdio.h>
#include <string.h>

#define CHECK(condition)       \
    do {                       \
        if (!(condition))      \
            return __LINE__;   \
    } while (0)

_Static_assert(sizeof(void *) == 8U, "The initial media ABI targets 64-bit platforms");
_Static_assert(sizeof(waddamburo_media_decoder_options) == 24U, "Decoder options ABI changed");
_Static_assert(sizeof(waddamburo_media_stream_info) == 40U, "Stream info ABI changed");
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

#if defined(WADDAMBURO_MEDIA_HAS_FFMPEG)
typedef struct memory_input {
    const uint8_t *bytes;
    uint64_t length;
    uint64_t position;
} memory_input;

static waddamburo_media_result WADDAMBURO_MEDIA_CALL memory_read(
    void *user_data,
    uint8_t *destination,
    uint64_t capacity,
    uint64_t *bytes_read)
{
    memory_input *input = user_data;
    uint64_t remaining = input->length - input->position;
    uint64_t count = remaining < capacity ? remaining : capacity;
    memcpy(destination, input->bytes + input->position, (size_t)count);
    input->position += count;
    *bytes_read = count;
    return count == 0U ? WADDAMBURO_MEDIA_END_OF_STREAM : WADDAMBURO_MEDIA_OK;
}

static waddamburo_media_result WADDAMBURO_MEDIA_CALL memory_seek(
    void *user_data,
    int64_t offset,
    uint32_t origin,
    uint64_t *position)
{
    memory_input *input = user_data;
    int64_t base = origin == WADDAMBURO_MEDIA_SEEK_BEGIN
                       ? 0
                       : origin == WADDAMBURO_MEDIA_SEEK_CURRENT ? (int64_t)input->position
                                                                 : (int64_t)input->length;
    if (offset < -base || offset > (int64_t)input->length - base)
        return WADDAMBURO_MEDIA_ERROR_IO;
    input->position = (uint64_t)(base + offset);
    *position = input->position;
    return WADDAMBURO_MEDIA_OK;
}

static int32_t WADDAMBURO_MEDIA_CALL cancel_callback(void *user_data)
{
    (void)user_data;
    return 1;
}

static int write_synthetic_nub(const char *path)
{
    static const uint8_t bytes[] = {
        'N','U','B','0', 0,0,0,0,
        'R','I','F','F', 108,0,0,0, 'W','A','V','E',
        'f','m','t',' ', 16,0,0,0, 1,0, 2,0, 0x40,0x1f,0,0,
        0x00,0x7d,0,0, 4,0, 16,0, 'd','a','t','a', 4,0,0,0,
        0,0, 0,0,
        's','m','p','l', 60,0,0,0,
        0,0,0,0, 0,0,0,0, 0,0,0,0, 60,0,0,0,
        0,0,0,0, 0,0,0,0, 0,0,0,0, 1,0,0,0,
        0,0,0,0,
        0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0,
        0,0,0,0, 0,0,0,0
    };
    FILE *file = fopen(path, "wb");
    if (file == NULL)
        return 0;
    if (fwrite(bytes, 1U, sizeof(bytes), file) != sizeof(bytes)) {
        fclose(file);
        return 0;
    }
    return fclose(file) == 0;
}
#endif

#if defined(WADDAMBURO_MEDIA_HAS_VGMSTREAM)
static void write_u16be(uint8_t *destination, uint16_t value)
{
    destination[0] = (uint8_t)(value >> 8U);
    destination[1] = (uint8_t)value;
}

static void write_u32be(uint8_t *destination, uint32_t value)
{
    destination[0] = (uint8_t)(value >> 24U);
    destination[1] = (uint8_t)(value >> 16U);
    destination[2] = (uint8_t)(value >> 8U);
    destination[3] = (uint8_t)value;
}

static int write_synthetic_bnsf(const char *path)
{
    uint8_t bytes[288] = {0};
    FILE *file = NULL;
    memcpy(bytes + 0, "BNSF", 4U);
    write_u32be(bytes + 4, sizeof(bytes));
    memcpy(bytes + 8, "IS22", 4U);
    memcpy(bytes + 12, "sfmt", 4U);
    write_u32be(bytes + 16, 20U);
    write_u16be(bytes + 20, 0U);
    write_u16be(bytes + 22, 2U);
    write_u32be(bytes + 24, 48000U);
    write_u32be(bytes + 28, 960U);
    write_u32be(bytes + 32, 0U);
    write_u16be(bytes + 36, 240U);
    write_u16be(bytes + 38, 960U);
    memcpy(bytes + 40, "sdat", 4U);
    write_u32be(bytes + 44, 240U);
#if defined(_WIN32)
    if (fopen_s(&file, path, "wb") != 0)
        return 0;
#else
    file = fopen(path, "wb");
#endif
    if (file == NULL)
        return 0;
    if (fwrite(bytes, 1U, sizeof(bytes), file) != sizeof(bytes)) {
        fclose(file);
        return 0;
    }
    return fclose(file) == 0;
}
#endif

int main(void)
{
    uint32_t negotiated = 0U;
    waddamburo_media_decoder *decoder = NULL;
    waddamburo_media_decoder_options options = {sizeof(options), 0U, 0U, 0U, 0U, 0U};
    waddamburo_media_error error = {0};
    waddamburo_media_io_callbacks callbacks = {0};

    CHECK(waddamburo_media_get_abi_version() == WADDAMBURO_MEDIA_ABI_VERSION_1_3);
    CHECK(waddamburo_media_negotiate_abi(WADDAMBURO_MEDIA_ABI_VERSION_1_0, &negotiated) ==
          WADDAMBURO_MEDIA_OK);
    CHECK(negotiated == WADDAMBURO_MEDIA_ABI_VERSION_1_0);
    CHECK(waddamburo_media_negotiate_abi(WADDAMBURO_MEDIA_ABI_VERSION(2U, 0U), &negotiated) ==
          WADDAMBURO_MEDIA_ERROR_ABI_MISMATCH);
    CHECK(negotiated == 0U);
    CHECK(waddamburo_media_negotiate_abi(WADDAMBURO_MEDIA_ABI_VERSION_1_1, &negotiated) ==
          WADDAMBURO_MEDIA_OK);
    CHECK(negotiated == WADDAMBURO_MEDIA_ABI_VERSION_1_1);
    CHECK(waddamburo_media_negotiate_abi(WADDAMBURO_MEDIA_ABI_VERSION_1_2, &negotiated) ==
          WADDAMBURO_MEDIA_OK);
    CHECK(negotiated == WADDAMBURO_MEDIA_ABI_VERSION_1_2);
    CHECK(waddamburo_media_negotiate_abi(WADDAMBURO_MEDIA_ABI_VERSION_1_3, &negotiated) ==
          WADDAMBURO_MEDIA_OK);
    CHECK(negotiated == WADDAMBURO_MEDIA_ABI_VERSION_1_3);
    CHECK(waddamburo_media_negotiate_abi(WADDAMBURO_MEDIA_ABI_VERSION(1U, 4U), &negotiated) ==
          WADDAMBURO_MEDIA_ERROR_ABI_MISMATCH);
    CHECK(negotiated == 0U);

    {
        waddamburo_media_video *video = NULL;
        waddamburo_media_video_info video_info = {sizeof(video_info), 0U, 0U, 0U, 0};
        error.struct_size = sizeof(error);
        CHECK(waddamburo_media_video_open_file("missing-synthetic.pam", &video, &video_info, &error) !=
              WADDAMBURO_MEDIA_OK);
        CHECK(video == NULL);
        CHECK(error.message_length == strlen(error.message));
        waddamburo_media_video_destroy(NULL);
    }

    error.struct_size = sizeof(error);
#if !defined(WADDAMBURO_MEDIA_HAS_FFMPEG)
    CHECK(waddamburo_media_decoder_create_file(&options, "synthetic.wav", &decoder, &error) ==
          WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE);
    CHECK(decoder == NULL);
    CHECK(error.code == WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE);
    CHECK(error.message_length == strlen(error.message));
    CHECK(strstr(error.message, "not built") != NULL);
#else
    CHECK(waddamburo_media_decoder_create_file(&options, "does-not-exist.wav", &decoder, &error) ==
          WADDAMBURO_MEDIA_ERROR_IO);
#endif

    decoder = (waddamburo_media_decoder *)(uintptr_t)1U;
    error.struct_size = 0U;
    CHECK(waddamburo_media_decoder_create_file(&options, "synthetic.wav", &decoder, &error) ==
          WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT);
    CHECK(decoder == NULL);

    error.struct_size = sizeof(error);
    callbacks.struct_size = sizeof(callbacks);
    callbacks.read = read_callback;
#if !defined(WADDAMBURO_MEDIA_HAS_FFMPEG)
    CHECK(waddamburo_media_decoder_create_callbacks(&options, &callbacks, &decoder, &error) ==
          WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE);
    CHECK(decoder == NULL);
#else
    {
        static const uint8_t wave[] = {
            'N','U','B','0', 0,0,0,0,
            'R','I','F','F', 40,0,0,0, 'W','A','V','E',
            'f','m','t',' ', 16,0,0,0, 1,0, 2,0, 0x40,0x1f,0,0,
            0x00,0x7d,0,0, 4,0, 16,0, 'd','a','t','a', 4,0,0,0,
            0,0, 0,0
        };
        memory_input input = {wave, sizeof(wave), 0U};
        waddamburo_media_stream_info info = {0};
        float samples[16] = {0};
        uint64_t frames_read = 0U;
        info.struct_size = sizeof(info);
        options.output_sample_rate = 8000U;
        options.output_channels = 2U;
        callbacks.user_data = &input;
        callbacks.read = memory_read;
        callbacks.seek = memory_seek;
        callbacks.should_cancel = cancel_callback;
        options.source_stream_index = 1U;
        CHECK(waddamburo_media_decoder_create_callbacks(&options, &callbacks, &decoder, &error) ==
              WADDAMBURO_MEDIA_ERROR_UNSUPPORTED);
        CHECK(decoder == NULL);
        options.source_stream_index = 0U;
        CHECK(waddamburo_media_decoder_create_callbacks(&options, &callbacks, &decoder, &error) ==
              WADDAMBURO_MEDIA_ERROR_CANCELLED);
        CHECK(decoder == NULL);
        input.position = 0U;
        callbacks.should_cancel = NULL;
        CHECK(waddamburo_media_decoder_create_callbacks(&options, &callbacks, &decoder, &error) ==
              WADDAMBURO_MEDIA_OK);
        CHECK(decoder != NULL);
        CHECK(waddamburo_media_decoder_get_stream_info(decoder, &info) == WADDAMBURO_MEDIA_OK);
        CHECK(info.sample_rate == 8000U);
        CHECK(info.channels == 2U);
        CHECK(waddamburo_media_decoder_read_frames(decoder, samples, 8U, &frames_read) ==
              WADDAMBURO_MEDIA_OK);
        CHECK(frames_read > 0U);
        CHECK(waddamburo_media_decoder_seek(decoder, 0U) == WADDAMBURO_MEDIA_OK);
        frames_read = 0U;
        CHECK(waddamburo_media_decoder_read_frames(decoder, samples, 8U, &frames_read) ==
              WADDAMBURO_MEDIA_OK);
        CHECK(frames_read > 0U);
        waddamburo_media_decoder_destroy(decoder);
        decoder = NULL;
    }
    {
        const char *path = "waddamburo-synthetic.nub";
        waddamburo_media_stream_info info = {0};
        info.struct_size = sizeof(info);
        options.output_sample_rate = 8000U;
        options.output_channels = 2U;
        CHECK(write_synthetic_nub(path));
        CHECK(waddamburo_media_decoder_create_file(&options, path, &decoder, &error) ==
              WADDAMBURO_MEDIA_OK);
        CHECK(decoder != NULL);
        CHECK(waddamburo_media_decoder_get_stream_info(decoder, &info) == WADDAMBURO_MEDIA_OK);
        CHECK(info.sample_rate == 8000U);
        CHECK(info.channels == 2U);
        CHECK(info.loop_start_frame == 0U);
        CHECK(info.loop_end_frame == 1U);
        waddamburo_media_decoder_destroy(decoder);
        decoder = NULL;
        CHECK(remove(path) == 0);
    }
#endif

#if defined(WADDAMBURO_MEDIA_HAS_VGMSTREAM)
    {
        const char *path = "waddamburo-synthetic.bnsf";
        waddamburo_media_stream_info info = {0};
        float samples[1920] = {0};
        uint64_t frames_read = 0U;
        info.struct_size = sizeof(info);
        options.output_sample_rate = 48000U;
        options.output_channels = 2U;
        CHECK(write_synthetic_bnsf(path));
        CHECK(waddamburo_media_decoder_create_file(&options, path, &decoder, &error) ==
              WADDAMBURO_MEDIA_OK);
        CHECK(decoder != NULL);
        CHECK(waddamburo_media_decoder_get_stream_info(decoder, &info) == WADDAMBURO_MEDIA_OK);
        CHECK(info.sample_rate == 48000U);
        CHECK(info.channels == 2U);
        CHECK(info.total_frames == 960U);
        CHECK(waddamburo_media_decoder_read_frames(decoder, samples, 960U, &frames_read) ==
              WADDAMBURO_MEDIA_OK);
        CHECK(frames_read == 960U);
        CHECK(waddamburo_media_decoder_seek(decoder, 0U) == WADDAMBURO_MEDIA_OK);
        waddamburo_media_decoder_destroy(decoder);
        decoder = NULL;
        CHECK(remove(path) == 0);
    }
#endif

    CHECK(waddamburo_media_decoder_cancel(NULL) == WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT);
    CHECK(waddamburo_media_decoder_seek(NULL, 0U) == WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT);
    waddamburo_media_decoder_destroy(NULL);
    return 0;
}
