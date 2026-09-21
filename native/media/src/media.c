#include "waddamburo/media.h"

#include <ctype.h>
#include <errno.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

#if defined(WADDAMBURO_MEDIA_HAS_FFMPEG)
#include <stdatomic.h>

#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
#include <libavutil/error.h>
#include <libavutil/mem.h>
#include <libavutil/samplefmt.h>
#include <libswresample/swresample.h>
#endif

#if defined(WADDAMBURO_MEDIA_HAS_VGMSTREAM)
#include <libvgmstream.h>
#endif

static int options_are_valid(const waddamburo_media_decoder_options *options)
{
    return options != NULL &&
           options->struct_size >= offsetof(waddamburo_media_decoder_options, source_stream_index) &&
           options->flags == 0U && options->output_channels <= 8U &&
           (options->struct_size < sizeof(*options) || options->reserved == 0U);
}

#if defined(WADDAMBURO_MEDIA_HAS_FFMPEG)
static uint32_t options_source_stream_index(const waddamburo_media_decoder_options *options)
{
    if (options->struct_size <
        offsetof(waddamburo_media_decoder_options, source_stream_index) + sizeof(uint32_t))
        return 0U;
    return options->source_stream_index;
}
#endif

static int error_is_valid(const waddamburo_media_error *error)
{
    return error != NULL && error->struct_size >= sizeof(*error);
}

static void set_error_details(
    waddamburo_media_error *error,
    waddamburo_media_result code,
    int32_t native_code,
    const char *message)
{
    size_t length;
    if (!error_is_valid(error))
        return;
    length = strlen(message);
    if (length >= sizeof(error->message))
        length = sizeof(error->message) - 1U;
    error->code = code;
    error->native_code = native_code;
    error->message_length = (uint32_t)length;
    memcpy(error->message, message, length);
    error->message[length] = '\0';
}

static void set_error(waddamburo_media_error *error, waddamburo_media_result code, const char *message)
{
    set_error_details(error, code, 0, message);
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
            WADDAMBURO_MEDIA_ABI_VERSION_MAJOR(WADDAMBURO_MEDIA_ABI_VERSION_CURRENT) ||
        WADDAMBURO_MEDIA_ABI_VERSION_MINOR(requested_version) >
            WADDAMBURO_MEDIA_ABI_VERSION_MINOR(WADDAMBURO_MEDIA_ABI_VERSION_CURRENT))
        return WADDAMBURO_MEDIA_ERROR_ABI_MISMATCH;
    *negotiated_version = requested_version;
    return WADDAMBURO_MEDIA_OK;
}

#if defined(WADDAMBURO_MEDIA_HAS_FFMPEG)

#define IO_BUFFER_SIZE 32768
#define NUB_SCAN_BUFFER_SIZE 65536

typedef struct media_input {
    FILE *file;
    waddamburo_media_io_callbacks callbacks;
    int uses_callbacks;
    int64_t view_offset;
    int64_t view_size;
    int64_t position;
} media_input;

struct waddamburo_media_decoder {
    enum {
        MEDIA_BACKEND_FFMPEG,
        MEDIA_BACKEND_VGMSTREAM
    } backend;
    media_input input;
    AVFormatContext *format;
    AVIOContext *io;
    AVCodecContext *codec;
    SwrContext *resampler;
    AVPacket *packet;
    AVFrame *frame;
    int audio_stream;
    int input_sample_rate;
    uint32_t output_sample_rate;
    uint32_t output_channels;
    uint64_t total_frames;
    uint64_t loop_start_frame;
    uint64_t loop_end_frame;
    float *pending;
    uint64_t pending_frames;
    uint64_t pending_offset;
    int demux_eof;
    int decoder_eof;
    int resampler_flushed;
#if defined(WADDAMBURO_MEDIA_HAS_VGMSTREAM)
    libvgmstream_t *vgmstream;
#endif
    atomic_bool cancelled;
    waddamburo_media_error error;
};

static void decoder_set_error(
    waddamburo_media_decoder *decoder,
    waddamburo_media_result code,
    int native_code,
    const char *message)
{
    char combined[256];
    if (native_code < 0) {
        char native_message[AV_ERROR_MAX_STRING_SIZE];
        av_strerror(native_code, native_message, sizeof(native_message));
        (void)snprintf(combined, sizeof(combined), "%s: %s", message, native_message);
        set_error_details(&decoder->error, code, native_code, combined);
    } else {
        set_error_details(&decoder->error, code, native_code, message);
    }
}

static int input_cancelled(const waddamburo_media_decoder *decoder)
{
    return atomic_load_explicit(&decoder->cancelled, memory_order_relaxed) ||
           (decoder->input.uses_callbacks && decoder->input.callbacks.should_cancel != NULL &&
            decoder->input.callbacks.should_cancel(decoder->input.callbacks.user_data) != 0);
}

static int input_read(media_input *input, uint8_t *destination, int capacity)
{
    uint64_t allowed = (uint64_t)capacity;
    if (input->view_size >= 0) {
        int64_t remaining = input->view_size - input->position;
        if (remaining <= 0)
            return AVERROR_EOF;
        if ((uint64_t)remaining < allowed)
            allowed = (uint64_t)remaining;
    }
    if (input->uses_callbacks) {
        uint64_t bytes_read = 0U;
        waddamburo_media_result result = input->callbacks.read(
            input->callbacks.user_data, destination, allowed, &bytes_read);
        if (bytes_read > allowed)
            return AVERROR(EIO);
        input->position += (int64_t)bytes_read;
        if (bytes_read > 0U)
            return (int)bytes_read;
        return result == WADDAMBURO_MEDIA_END_OF_STREAM || result == WADDAMBURO_MEDIA_OK
                   ? AVERROR_EOF
                   : AVERROR(EIO);
    }
    {
        size_t bytes_read = fread(destination, 1U, (size_t)allowed, input->file);
        input->position += (int64_t)bytes_read;
        if (bytes_read > 0U)
            return (int)bytes_read;
        return feof(input->file) ? AVERROR_EOF : AVERROR(EIO);
    }
}

static int64_t input_seek_absolute(media_input *input, int64_t absolute_position)
{
    uint64_t new_position = 0U;
    if (absolute_position < 0)
        return AVERROR(EINVAL);
    if (input->uses_callbacks) {
        if (input->callbacks.seek == NULL ||
            input->callbacks.seek(input->callbacks.user_data, absolute_position,
                                  WADDAMBURO_MEDIA_SEEK_BEGIN, &new_position) != WADDAMBURO_MEDIA_OK ||
            new_position != (uint64_t)absolute_position)
            return AVERROR(EIO);
    } else {
#if defined(_WIN32)
        if (_fseeki64(input->file, absolute_position, SEEK_SET) != 0)
#else
        if (fseeko(input->file, (off_t)absolute_position, SEEK_SET) != 0)
#endif
            return AVERROR(errno);
    }
    input->position = absolute_position - input->view_offset;
    return input->position;
}

static int64_t input_size(media_input *input)
{
    if (input->view_size >= 0)
        return input->view_size;
    if (input->uses_callbacks) {
        uint64_t original = 0U;
        uint64_t end = 0U;
        uint64_t restored = 0U;
        if (input->callbacks.seek == NULL ||
            input->callbacks.seek(input->callbacks.user_data, 0, WADDAMBURO_MEDIA_SEEK_CURRENT,
                                  &original) != WADDAMBURO_MEDIA_OK)
            return AVERROR(ENOSYS);
        if (input->callbacks.seek(input->callbacks.user_data, 0, WADDAMBURO_MEDIA_SEEK_END, &end) !=
            WADDAMBURO_MEDIA_OK) {
            (void)input->callbacks.seek(input->callbacks.user_data, (int64_t)original,
                                        WADDAMBURO_MEDIA_SEEK_BEGIN, &restored);
            return AVERROR(ENOSYS);
        }
        if (input->callbacks.seek(input->callbacks.user_data, (int64_t)original,
                                  WADDAMBURO_MEDIA_SEEK_BEGIN, &restored) != WADDAMBURO_MEDIA_OK ||
            restored != original || end > INT64_MAX)
            return AVERROR(ENOSYS);
        return (int64_t)end;
    }
    {
#if defined(_WIN32)
        int64_t original = _ftelli64(input->file);
        int64_t end;
        if (original < 0 || _fseeki64(input->file, 0, SEEK_END) != 0)
            return AVERROR(errno);
        end = _ftelli64(input->file);
        if (_fseeki64(input->file, original, SEEK_SET) != 0)
            return AVERROR(errno);
#else
        off_t original = ftello(input->file);
        off_t end;
        if (original < 0 || fseeko(input->file, 0, SEEK_END) != 0)
            return AVERROR(errno);
        end = ftello(input->file);
        if (fseeko(input->file, original, SEEK_SET) != 0)
            return AVERROR(errno);
#endif
        return end < 0 ? AVERROR(errno) : (int64_t)end;
    }
}

static int ffmpeg_read(void *opaque, uint8_t *destination, int capacity)
{
    waddamburo_media_decoder *decoder = opaque;
    return input_cancelled(decoder) ? AVERROR_EXIT
                                    : input_read(&decoder->input, destination, capacity);
}

static int64_t ffmpeg_seek(void *opaque, int64_t offset, int whence)
{
    waddamburo_media_decoder *decoder = opaque;
    media_input *input = &decoder->input;
    int base_whence = whence & ~AVSEEK_FORCE;
    int64_t target;
    if (input_cancelled(decoder))
        return AVERROR_EXIT;
    if (base_whence == AVSEEK_SIZE)
        return input_size(input);
    if (base_whence == SEEK_SET)
        target = offset;
    else if (base_whence == SEEK_CUR)
        target = input->position + offset;
    else if (base_whence == SEEK_END) {
        int64_t size = input_size(input);
        if (size < 0)
            return size;
        target = size + offset;
    } else {
        return AVERROR(EINVAL);
    }
    if (target < 0 || (input->view_size >= 0 && target > input->view_size))
        return AVERROR(EINVAL);
    return input_seek_absolute(input, input->view_offset + target);
}

static uint32_t read_u32le(const uint8_t *value)
{
    return (uint32_t)value[0] | ((uint32_t)value[1] << 8U) | ((uint32_t)value[2] << 16U) |
           ((uint32_t)value[3] << 24U);
}

static FILE *open_utf8_file(const char *path);

/* Some stock NUBs carry ATRAC loop metadata in the enclosing RIFF `smpl` chunk,
 * outside the elementary stream understood by vgmstream. */
static void discover_file_riff_loop(const char *path, waddamburo_media_decoder *decoder)
{
    FILE *file = open_utf8_file(path);
    uint8_t scan[NUB_SCAN_BUFFER_SIZE];
    size_t count;
    size_t index;
    uint64_t riff_offset = 0U;
    uint64_t riff_end = 0U;
    if (file == NULL)
        return;
    count = fread(scan, 1U, sizeof(scan), file);
    for (index = 0U; index + 12U <= count; ++index) {
        if (memcmp(scan + index, "RIFF", 4U) == 0 &&
            memcmp(scan + index + 8U, "WAVE", 4U) == 0) {
            riff_offset = index;
            riff_end = riff_offset + (uint64_t)read_u32le(scan + index + 4U) + 8U;
            break;
        }
    }
    if (riff_end <= riff_offset + 12U)
        goto done;
    {
        uint64_t offset = riff_offset + 12U;
        while (offset + 8U <= riff_end) {
            uint8_t header[8];
            uint32_t chunk_size;
            if (offset > (uint64_t)INT64_MAX ||
#if defined(_WIN32)
                _fseeki64(file, (int64_t)offset, SEEK_SET) != 0 ||
#else
                fseeko(file, (off_t)offset, SEEK_SET) != 0 ||
#endif
                fread(header, 1U, sizeof(header), file) != sizeof(header))
                break;
            chunk_size = read_u32le(header + 4U);
            if (memcmp(header, "smpl", 4U) == 0 && chunk_size >= 60U) {
                uint8_t payload[60];
                if (fread(payload, 1U, sizeof(payload), file) == sizeof(payload)) {
                    uint32_t loop_count = read_u32le(payload + 28U);
                    uint32_t start = read_u32le(payload + 44U);
                    uint32_t end = read_u32le(payload + 48U);
                    if (loop_count != 0U && end >= start && decoder->input_sample_rate > 0) {
                        decoder->loop_start_frame = (uint64_t)av_rescale(
                            start, decoder->output_sample_rate, decoder->input_sample_rate);
                        decoder->loop_end_frame = (uint64_t)av_rescale(
                            (uint64_t)end + 1U,
                            decoder->output_sample_rate, decoder->input_sample_rate);
                    }
                }
                break;
            }
            offset += 8U + (uint64_t)chunk_size + (chunk_size & 1U);
            if (offset > riff_end)
                break;
        }
    }
done:
    fclose(file);
}

/* NUB is treated only as a bounded carrier for a complete RIFF/WAVE stream. */
static int discover_riff_view(waddamburo_media_decoder *decoder)
{
    uint8_t *buffer;
    int64_t size = input_size(&decoder->input);
    int64_t scan_size;
    int64_t scanned = 0;
    int carry = 0;
    if (size < 12 || input_seek_absolute(&decoder->input, 0) < 0)
        return 0;
    scan_size = size < NUB_SCAN_BUFFER_SIZE ? size : NUB_SCAN_BUFFER_SIZE;
    buffer = av_malloc(NUB_SCAN_BUFFER_SIZE + 11U);
    if (buffer == NULL)
        return AVERROR(ENOMEM);
    while (scanned < scan_size) {
        int request = (int)((scan_size - scanned) < NUB_SCAN_BUFFER_SIZE
                                ? scan_size - scanned
                                : NUB_SCAN_BUFFER_SIZE);
        int count;
        int total;
        int index;
        if (input_cancelled(decoder)) {
            av_free(buffer);
            return AVERROR_EXIT;
        }
        count = input_read(&decoder->input, buffer + carry, request);
        if (count <= 0)
            break;
        total = carry + count;
        for (index = 0; index + 12 <= total; ++index) {
            if (memcmp(buffer + index, "RIFF", 4U) == 0 &&
                memcmp(buffer + index + 8, "WAVE", 4U) == 0) {
                uint64_t riff_size = (uint64_t)read_u32le(buffer + index + 4) + 8U;
                int64_t offset = scanned - carry + index;
                if (riff_size >= 12U && riff_size <= (uint64_t)(size - offset)) {
                    decoder->input.view_offset = offset;
                    decoder->input.view_size = (int64_t)riff_size;
                    decoder->input.position = 0;
                    av_free(buffer);
                    return input_seek_absolute(&decoder->input, offset) < 0 ? AVERROR(EIO) : 1;
                }
            }
        }
        carry = total < 11 ? total : 11;
        memmove(buffer, buffer + total - carry, (size_t)carry);
        scanned += count;
    }
    av_free(buffer);
    decoder->input.view_offset = 0;
    decoder->input.view_size = -1;
    decoder->input.position = 0;
    return input_seek_absolute(&decoder->input, 0) < 0 ? AVERROR(EIO) : 0;
}

static int ffmpeg_interrupt(void *opaque)
{
    return input_cancelled((const waddamburo_media_decoder *)opaque);
}

static void decoder_destroy_internal(waddamburo_media_decoder *decoder)
{
    if (decoder == NULL)
        return;
    av_free(decoder->pending);
#if defined(WADDAMBURO_MEDIA_HAS_VGMSTREAM)
    libvgmstream_free(decoder->vgmstream);
#endif
    av_frame_free(&decoder->frame);
    av_packet_free(&decoder->packet);
    swr_free(&decoder->resampler);
    avcodec_free_context(&decoder->codec);
    if (decoder->format != NULL)
        avformat_close_input(&decoder->format);
    if (decoder->io != NULL) {
        av_freep(&decoder->io->buffer);
        avio_context_free(&decoder->io);
    }
    if (decoder->input.file != NULL)
        fclose(decoder->input.file);
    free(decoder);
}

static waddamburo_media_result decoder_open(
    waddamburo_media_decoder *decoder,
    const waddamburo_media_decoder_options *options,
    waddamburo_media_decoder **result,
    waddamburo_media_error *error)
{
    AVStream *stream;
    const AVCodec *codec;
    AVChannelLayout output_layout = {0};
    uint8_t *io_buffer;
    int native_result;
    decoder->error.struct_size = sizeof(decoder->error);
    decoder->backend = MEDIA_BACKEND_FFMPEG;
    decoder->input.view_size = -1;
    decoder->total_frames = WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT;
    decoder->loop_start_frame = WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT;
    decoder->loop_end_frame = WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT;
    atomic_init(&decoder->cancelled, false);

    native_result = discover_riff_view(decoder);
    if (native_result < 0) {
        decoder_set_error(decoder, native_result == AVERROR_EXIT ? WADDAMBURO_MEDIA_ERROR_CANCELLED
                                                                 : WADDAMBURO_MEDIA_ERROR_IO,
                          native_result, "Could not inspect the audio input");
        goto fail;
    }
    decoder->format = avformat_alloc_context();
    io_buffer = av_malloc(IO_BUFFER_SIZE);
    if (decoder->format == NULL || io_buffer == NULL) {
        av_free(io_buffer);
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, AVERROR(ENOMEM),
                          "Could not allocate the FFmpeg input");
        goto fail;
    }
    decoder->io = avio_alloc_context(
        io_buffer, IO_BUFFER_SIZE, 0, decoder, ffmpeg_read, NULL,
        decoder->input.uses_callbacks && decoder->input.callbacks.seek == NULL ? NULL : ffmpeg_seek);
    if (decoder->io == NULL) {
        av_free(io_buffer);
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, AVERROR(ENOMEM),
                          "Could not allocate the FFmpeg I/O context");
        goto fail;
    }
    decoder->format->pb = decoder->io;
    decoder->format->flags |= AVFMT_FLAG_CUSTOM_IO;
    decoder->format->interrupt_callback.callback = ffmpeg_interrupt;
    decoder->format->interrupt_callback.opaque = decoder;
    native_result = avformat_open_input(&decoder->format, NULL, NULL, NULL);
    if (native_result < 0) {
        decoder_set_error(decoder, native_result == AVERROR_EXIT ? WADDAMBURO_MEDIA_ERROR_CANCELLED
                                                                 : WADDAMBURO_MEDIA_ERROR_UNSUPPORTED,
                          native_result,
                          "FFmpeg could not open the audio input");
        goto fail;
    }
    native_result = avformat_find_stream_info(decoder->format, NULL);
    if (native_result < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, native_result,
                          "FFmpeg could not read audio stream information");
        goto fail;
    }
    native_result = av_find_best_stream(decoder->format, AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
    if (native_result < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, native_result,
                          "The input has no supported audio stream");
        goto fail;
    }
    decoder->audio_stream = native_result;
    stream = decoder->format->streams[decoder->audio_stream];
    decoder->codec = avcodec_alloc_context3(codec);
    if (decoder->codec == NULL) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, AVERROR(ENOMEM),
                          "Could not allocate the audio codec");
        goto fail;
    }
    native_result = avcodec_parameters_to_context(decoder->codec, stream->codecpar);
    if (native_result >= 0)
        native_result = avcodec_open2(decoder->codec, codec, NULL);
    if (native_result < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, native_result,
                          "FFmpeg could not initialize the audio codec");
        goto fail;
    }

    decoder->output_sample_rate = options->output_sample_rate != 0U
                                      ? options->output_sample_rate
                                      : (uint32_t)decoder->codec->sample_rate;
    decoder->output_channels = options->output_channels != 0U
                                   ? options->output_channels
                                   : (uint32_t)decoder->codec->ch_layout.nb_channels;
    decoder->input_sample_rate = decoder->codec->sample_rate;
    if (decoder->output_sample_rate == 0U || decoder->output_sample_rate > 768000U ||
        decoder->output_channels == 0U || decoder->output_channels > 8U) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, 0,
                          "The requested output format is unsupported");
        goto fail;
    }
    av_channel_layout_default(&output_layout, (int)decoder->output_channels);
    native_result = swr_alloc_set_opts2(&decoder->resampler, &output_layout, AV_SAMPLE_FMT_FLT,
                                        (int)decoder->output_sample_rate, &decoder->codec->ch_layout,
                                        decoder->codec->sample_fmt, decoder->codec->sample_rate, 0, NULL);
    av_channel_layout_uninit(&output_layout);
    if (native_result >= 0)
        native_result = swr_init(decoder->resampler);
    if (native_result < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, native_result,
                          "FFmpeg could not initialize audio conversion");
        goto fail;
    }
    decoder->packet = av_packet_alloc();
    decoder->frame = av_frame_alloc();
    if (decoder->packet == NULL || decoder->frame == NULL) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, AVERROR(ENOMEM),
                          "Could not allocate decoder buffers");
        goto fail;
    }
    if (stream->duration != AV_NOPTS_VALUE && stream->duration >= 0)
        decoder->total_frames = (uint64_t)av_rescale_q(
            stream->duration, stream->time_base, (AVRational){1, (int)decoder->output_sample_rate});
    else if (decoder->format->duration != AV_NOPTS_VALUE && decoder->format->duration >= 0)
        decoder->total_frames = (uint64_t)av_rescale_q(
            decoder->format->duration, AV_TIME_BASE_Q,
            (AVRational){1, (int)decoder->output_sample_rate});
    *result = decoder;
    set_error(error, WADDAMBURO_MEDIA_OK, "");
    return WADDAMBURO_MEDIA_OK;

fail:
    *error = decoder->error;
    decoder_destroy_internal(decoder);
    return error->code;
}

#if defined(WADDAMBURO_MEDIA_HAS_VGMSTREAM)
static int ascii_equal_ignore_case(const char *left, const char *right)
{
    while (*left != '\0' && *right != '\0') {
        if (tolower((unsigned char)*left) != tolower((unsigned char)*right))
            return 0;
        ++left;
        ++right;
    }
    return *left == *right;
}

static int path_has_extension(const char *path, const char *expected)
{
    const char *extension = strrchr(path, '.');
    if (extension == NULL)
        return 0;
    return ascii_equal_ignore_case(extension + 1, expected);
}

static int path_uses_vgmstream(const char *path)
{
    return path_has_extension(path, "nus3bank") ||
           path_has_extension(path, "nus3audio") ||
           path_has_extension(path, "nub") ||
           path_has_extension(path, "bnsf") ||
           path_has_extension(path, "spsis14") ||
           path_has_extension(path, "spsis22") ||
           path_has_extension(path, "idsp");
}

static waddamburo_media_result vgmstream_open_file(
    waddamburo_media_decoder *decoder,
    const waddamburo_media_decoder_options *options,
    const char *path,
    waddamburo_media_decoder **result,
    waddamburo_media_error *error)
{
    libstreamfile_t *stream_file;
    libvgmstream_config_t config = {0};
    AVChannelLayout input_layout = {0};
    AVChannelLayout output_layout = {0};
    int native_result;

    decoder->error.struct_size = sizeof(decoder->error);
    decoder->backend = MEDIA_BACKEND_VGMSTREAM;
    decoder->total_frames = WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT;
    decoder->loop_start_frame = WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT;
    decoder->loop_end_frame = WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT;
    atomic_init(&decoder->cancelled, false);
    if ((libvgmstream_get_version() >> 24U) != LIBVGMSTREAM_API_VERSION_MAJOR) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE, 0,
                          "The vgmstream runtime ABI is incompatible");
        goto fail;
    }

    stream_file = libstreamfile_open_from_stdio(path);
    if (stream_file == NULL) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_IO, errno,
                          "vgmstream could not open the audio file");
        goto fail;
    }
    config.ignore_loop = true;
    config.force_sfmt = LIBVGMSTREAM_SFMT_FLOAT;
    decoder->vgmstream = libvgmstream_create(
        stream_file, (int)options_source_stream_index(options), &config);
    libstreamfile_close(stream_file);
    if (decoder->vgmstream == NULL) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, 0,
                          "vgmstream could not recognize the audio stream");
        goto fail;
    }

    decoder->input_sample_rate = decoder->vgmstream->format->sample_rate;
    decoder->output_sample_rate = options->output_sample_rate != 0U
                                      ? options->output_sample_rate
                                      : (uint32_t)decoder->input_sample_rate;
    decoder->output_channels = options->output_channels != 0U
                                   ? options->output_channels
                                   : (uint32_t)decoder->vgmstream->format->channels;
    if (decoder->input_sample_rate <= 0 || decoder->vgmstream->format->channels <= 0 ||
        decoder->output_sample_rate == 0U || decoder->output_sample_rate > 768000U ||
        decoder->output_channels == 0U || decoder->output_channels > 8U) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, 0,
                          "The requested vgmstream output format is unsupported");
        goto fail;
    }

    av_channel_layout_default(&input_layout, decoder->vgmstream->format->channels);
    av_channel_layout_default(&output_layout, (int)decoder->output_channels);
    native_result = swr_alloc_set_opts2(&decoder->resampler, &output_layout, AV_SAMPLE_FMT_FLT,
                                        (int)decoder->output_sample_rate, &input_layout,
                                        AV_SAMPLE_FMT_FLT, decoder->input_sample_rate, 0, NULL);
    av_channel_layout_uninit(&input_layout);
    av_channel_layout_uninit(&output_layout);
    if (native_result >= 0)
        native_result = swr_init(decoder->resampler);
    if (native_result < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, native_result,
                          "FFmpeg could not initialize vgmstream output conversion");
        goto fail;
    }
    if (decoder->vgmstream->format->play_samples >= 0)
        decoder->total_frames = (uint64_t)av_rescale(
            decoder->vgmstream->format->play_samples,
            decoder->output_sample_rate, decoder->input_sample_rate);
    if (decoder->vgmstream->format->loop_start >= 0 &&
        decoder->vgmstream->format->loop_end > decoder->vgmstream->format->loop_start) {
        decoder->loop_start_frame = (uint64_t)av_rescale(
            decoder->vgmstream->format->loop_start,
            decoder->output_sample_rate, decoder->input_sample_rate);
        decoder->loop_end_frame = (uint64_t)av_rescale(
            decoder->vgmstream->format->loop_end,
            decoder->output_sample_rate, decoder->input_sample_rate);
    }
    if (decoder->loop_start_frame == WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT)
        discover_file_riff_loop(path, decoder);
    *result = decoder;
    set_error(error, WADDAMBURO_MEDIA_OK, "");
    return WADDAMBURO_MEDIA_OK;

fail:
    *error = decoder->error;
    decoder_destroy_internal(decoder);
    return error->code;
}
#endif

static FILE *open_utf8_file(const char *path)
{
#if defined(_WIN32)
    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, NULL, 0);
    wchar_t *wide_path;
    FILE *file;
    if (length <= 0)
        return NULL;
    wide_path = malloc((size_t)length * sizeof(*wide_path));
    if (wide_path == NULL)
        return NULL;
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide_path, length) <= 0) {
        free(wide_path);
        return NULL;
    }
    file = _wfopen(wide_path, L"rb");
    free(wide_path);
    return file;
#else
    return fopen(path, "rb");
#endif
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_create_file(
    const waddamburo_media_decoder_options *options,
    const char *utf8_path,
    waddamburo_media_decoder **decoder,
    waddamburo_media_error *error)
{
    waddamburo_media_decoder *created;
    if (decoder == NULL)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    *decoder = NULL;
    if (!error_is_valid(error))
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    if (!options_are_valid(options) || utf8_path == NULL || utf8_path[0] == '\0') {
        set_error(error, WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT, "Invalid decoder arguments.");
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    }
    created = calloc(1U, sizeof(*created));
    if (created == NULL) {
        set_error(error, WADDAMBURO_MEDIA_ERROR_INTERNAL, "Could not allocate the decoder.");
        return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }
#if defined(WADDAMBURO_MEDIA_HAS_VGMSTREAM)
    if (path_uses_vgmstream(utf8_path)) {
        waddamburo_media_result vgmstream_result =
            vgmstream_open_file(created, options, utf8_path, decoder, error);
        if (vgmstream_result != WADDAMBURO_MEDIA_ERROR_UNSUPPORTED ||
            options_source_stream_index(options) != 0U ||
            !path_has_extension(utf8_path, "nub"))
            return vgmstream_result;
        created = calloc(1U, sizeof(*created));
        if (created == NULL) {
            set_error(error, WADDAMBURO_MEDIA_ERROR_INTERNAL, "Could not allocate the decoder.");
            return WADDAMBURO_MEDIA_ERROR_INTERNAL;
        }
    }
#endif
    if (options_source_stream_index(options) != 0U) {
        set_error(error, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED,
                  "Selected-stream decoding is unavailable for this input backend.");
        free(created);
        return WADDAMBURO_MEDIA_ERROR_UNSUPPORTED;
    }
    created->input.file = open_utf8_file(utf8_path);
    if (created->input.file == NULL) {
        set_error_details(error, WADDAMBURO_MEDIA_ERROR_IO, errno, "Could not open the audio file.");
        free(created);
        return WADDAMBURO_MEDIA_ERROR_IO;
    }
    {
        waddamburo_media_result result = decoder_open(created, options, decoder, error);
        if (result == WADDAMBURO_MEDIA_OK &&
            created->loop_start_frame == WADDAMBURO_MEDIA_UNKNOWN_FRAME_COUNT)
            discover_file_riff_loop(utf8_path, created);
        return result;
    }
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_create_callbacks(
    const waddamburo_media_decoder_options *options,
    const waddamburo_media_io_callbacks *callbacks,
    waddamburo_media_decoder **decoder,
    waddamburo_media_error *error)
{
    waddamburo_media_decoder *created;
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
    if (options_source_stream_index(options) != 0U) {
        set_error(error, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED,
                  "Selected-stream decoding is unavailable for callback inputs.");
        return WADDAMBURO_MEDIA_ERROR_UNSUPPORTED;
    }
    created = calloc(1U, sizeof(*created));
    if (created == NULL) {
        set_error(error, WADDAMBURO_MEDIA_ERROR_INTERNAL, "Could not allocate the decoder.");
        return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }
    created->input.callbacks = *callbacks;
    created->input.uses_callbacks = 1;
    return decoder_open(created, options, decoder, error);
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_get_stream_info(
    waddamburo_media_decoder *decoder,
    waddamburo_media_stream_info *stream_info)
{
    const size_t base_size = offsetof(waddamburo_media_stream_info, total_frames) +
                             sizeof(stream_info->total_frames);
    if (decoder == NULL || stream_info == NULL || stream_info->struct_size < base_size)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    stream_info->sample_rate = decoder->output_sample_rate;
    stream_info->channels = decoder->output_channels;
    stream_info->reserved = 0U;
    stream_info->total_frames = decoder->total_frames;
    if (stream_info->struct_size >= sizeof(*stream_info)) {
        stream_info->loop_start_frame = decoder->loop_start_frame;
        stream_info->loop_end_frame = decoder->loop_end_frame;
    }
    return WADDAMBURO_MEDIA_OK;
}

static waddamburo_media_result convert_frame(waddamburo_media_decoder *decoder)
{
    int capacity = (int)av_rescale_rnd(
        swr_get_delay(decoder->resampler, decoder->input_sample_rate) + decoder->frame->nb_samples,
        decoder->output_sample_rate, decoder->input_sample_rate, AV_ROUND_UP);
    uint8_t *output;
    int converted;
    if (capacity <= 0)
        return WADDAMBURO_MEDIA_OK;
    decoder->pending = av_realloc_f(decoder->pending,
                                    (size_t)capacity * decoder->output_channels,
                                    sizeof(*decoder->pending));
    if (decoder->pending == NULL) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, AVERROR(ENOMEM),
                          "Could not allocate decoded audio");
        return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }
    output = (uint8_t *)decoder->pending;
    converted = swr_convert(decoder->resampler, &output, capacity,
                            (const uint8_t *const *)decoder->frame->extended_data,
                            decoder->frame->nb_samples);
    if (converted < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, converted,
                          "FFmpeg could not convert decoded audio");
        return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }
    decoder->pending_frames = (uint64_t)converted;
    decoder->pending_offset = 0U;
    return WADDAMBURO_MEDIA_OK;
}

static waddamburo_media_result flush_resampler(waddamburo_media_decoder *decoder)
{
    int64_t delay = swr_get_delay(decoder->resampler, decoder->input_sample_rate);
    int capacity = (int)av_rescale_rnd(delay, decoder->output_sample_rate,
                                       decoder->input_sample_rate, AV_ROUND_UP);
    uint8_t *output;
    int converted;
    decoder->resampler_flushed = 1;
    if (capacity <= 0)
        return WADDAMBURO_MEDIA_END_OF_STREAM;
    decoder->pending = av_realloc_f(decoder->pending,
                                    (size_t)capacity * decoder->output_channels,
                                    sizeof(*decoder->pending));
    if (decoder->pending == NULL) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, AVERROR(ENOMEM),
                          "Could not allocate resampled audio");
        return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }
    output = (uint8_t *)decoder->pending;
    converted = swr_convert(decoder->resampler, &output, capacity, NULL, 0);
    if (converted < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, converted,
                          "FFmpeg could not flush audio conversion");
        return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }
    decoder->pending_frames = (uint64_t)converted;
    decoder->pending_offset = 0U;
    return converted > 0 ? WADDAMBURO_MEDIA_OK : WADDAMBURO_MEDIA_END_OF_STREAM;
}

static waddamburo_media_result decode_more_ffmpeg(waddamburo_media_decoder *decoder)
{
    int result;
    for (;;) {
        if (input_cancelled(decoder)) {
            decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_CANCELLED, AVERROR_EXIT,
                              "Audio decoding was cancelled");
            return WADDAMBURO_MEDIA_ERROR_CANCELLED;
        }
        result = avcodec_receive_frame(decoder->codec, decoder->frame);
        if (result == 0)
            return convert_frame(decoder);
        if (result == AVERROR_EOF) {
            waddamburo_media_result flush_result = decoder->resampler_flushed
                                                       ? WADDAMBURO_MEDIA_END_OF_STREAM
                                                       : flush_resampler(decoder);
            if (flush_result == WADDAMBURO_MEDIA_OK)
                return flush_result;
            if (flush_result != WADDAMBURO_MEDIA_END_OF_STREAM)
                return flush_result;
            decoder->decoder_eof = 1;
            return WADDAMBURO_MEDIA_END_OF_STREAM;
        }
        if (result != AVERROR(EAGAIN)) {
            decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, result,
                              "FFmpeg could not decode the audio frame");
            return WADDAMBURO_MEDIA_ERROR_UNSUPPORTED;
        }
        if (decoder->demux_eof) {
            result = avcodec_send_packet(decoder->codec, NULL);
            if (result == AVERROR_EOF) {
                decoder->decoder_eof = 1;
                return WADDAMBURO_MEDIA_END_OF_STREAM;
            }
            if (result < 0 && result != AVERROR(EAGAIN)) {
                decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, result,
                                  "FFmpeg could not flush the audio decoder");
                return WADDAMBURO_MEDIA_ERROR_INTERNAL;
            }
            continue;
        }
        result = av_read_frame(decoder->format, decoder->packet);
        if (result == AVERROR_EOF) {
            decoder->demux_eof = 1;
            continue;
        }
        if (result < 0) {
            decoder_set_error(decoder, result == AVERROR_EXIT ? WADDAMBURO_MEDIA_ERROR_CANCELLED
                                                               : WADDAMBURO_MEDIA_ERROR_IO,
                              result, "FFmpeg could not read the audio input");
            return result == AVERROR_EXIT ? WADDAMBURO_MEDIA_ERROR_CANCELLED
                                          : WADDAMBURO_MEDIA_ERROR_IO;
        }
        if (decoder->packet->stream_index == decoder->audio_stream) {
            result = avcodec_send_packet(decoder->codec, decoder->packet);
            av_packet_unref(decoder->packet);
            if (result < 0 && result != AVERROR(EAGAIN)) {
                decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, result,
                                  "FFmpeg rejected an audio packet");
                return WADDAMBURO_MEDIA_ERROR_UNSUPPORTED;
            }
        } else {
            av_packet_unref(decoder->packet);
        }
    }
}

#if defined(WADDAMBURO_MEDIA_HAS_VGMSTREAM)
static waddamburo_media_result decode_more_vgmstream(waddamburo_media_decoder *decoder)
{
    int native_result;
    int capacity;
    int converted;
    uint8_t *output;
    const uint8_t *input;
    int input_frames;

    if (input_cancelled(decoder)) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_CANCELLED, AVERROR_EXIT,
                          "Audio decoding was cancelled");
        return WADDAMBURO_MEDIA_ERROR_CANCELLED;
    }
    if (decoder->demux_eof) {
        waddamburo_media_result flush_result = decoder->resampler_flushed
                                                   ? WADDAMBURO_MEDIA_END_OF_STREAM
                                                   : flush_resampler(decoder);
        if (flush_result == WADDAMBURO_MEDIA_END_OF_STREAM)
            decoder->decoder_eof = 1;
        return flush_result;
    }
    native_result = libvgmstream_render(decoder->vgmstream);
    if (native_result < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, 0,
                          "vgmstream could not decode the audio stream");
        return WADDAMBURO_MEDIA_ERROR_UNSUPPORTED;
    }
    input_frames = decoder->vgmstream->decoder->buf_samples;
    if (input_frames <= 0) {
        if (decoder->vgmstream->decoder->done) {
            waddamburo_media_result flush_result = decoder->resampler_flushed
                                                       ? WADDAMBURO_MEDIA_END_OF_STREAM
                                                       : flush_resampler(decoder);
            if (flush_result == WADDAMBURO_MEDIA_END_OF_STREAM)
                decoder->decoder_eof = 1;
            return flush_result;
        }
        return WADDAMBURO_MEDIA_OK;
    }
    capacity = (int)av_rescale_rnd(
        swr_get_delay(decoder->resampler, decoder->input_sample_rate) + input_frames,
        decoder->output_sample_rate, decoder->input_sample_rate, AV_ROUND_UP);
    decoder->pending = av_realloc_f(decoder->pending,
                                    (size_t)capacity * decoder->output_channels,
                                    sizeof(*decoder->pending));
    if (decoder->pending == NULL) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, AVERROR(ENOMEM),
                          "Could not allocate vgmstream decoded audio");
        return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }
    output = (uint8_t *)decoder->pending;
    input = decoder->vgmstream->decoder->buf;
    converted = swr_convert(decoder->resampler, &output, capacity, &input, input_frames);
    if (converted < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, converted,
                          "FFmpeg could not convert vgmstream decoded audio");
        return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }
    decoder->pending_frames = (uint64_t)converted;
    decoder->pending_offset = 0U;
    if (decoder->vgmstream->decoder->done)
        decoder->demux_eof = 1;
    return WADDAMBURO_MEDIA_OK;
}
#endif

static waddamburo_media_result decode_more(waddamburo_media_decoder *decoder)
{
#if defined(WADDAMBURO_MEDIA_HAS_VGMSTREAM)
    if (decoder->backend == MEDIA_BACKEND_VGMSTREAM)
        return decode_more_vgmstream(decoder);
#endif
    return decode_more_ffmpeg(decoder);
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
    while (*frames_read < frame_capacity) {
        uint64_t available = decoder->pending_frames - decoder->pending_offset;
        if (available > 0U) {
            uint64_t count = frame_capacity - *frames_read;
            if (count > available)
                count = available;
            memcpy(interleaved_samples + (*frames_read * decoder->output_channels),
                   decoder->pending + (decoder->pending_offset * decoder->output_channels),
                   (size_t)(count * decoder->output_channels) * sizeof(*interleaved_samples));
            decoder->pending_offset += count;
            *frames_read += count;
            continue;
        }
        decoder->pending_frames = 0U;
        decoder->pending_offset = 0U;
        if (decoder->decoder_eof)
            break;
        {
            waddamburo_media_result result = decode_more(decoder);
            if (result != WADDAMBURO_MEDIA_OK && result != WADDAMBURO_MEDIA_END_OF_STREAM)
                return result;
        }
    }
    return *frames_read == 0U && decoder->decoder_eof ? WADDAMBURO_MEDIA_END_OF_STREAM
                                                      : WADDAMBURO_MEDIA_OK;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL
waddamburo_media_decoder_seek(waddamburo_media_decoder *decoder, uint64_t frame_index)
{
    AVStream *stream;
    int64_t timestamp;
    int result;
    if (decoder == NULL)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
#if defined(WADDAMBURO_MEDIA_HAS_VGMSTREAM)
    if (decoder->backend == MEDIA_BACKEND_VGMSTREAM) {
        int64_t input_frame;
        if (frame_index > INT64_MAX)
            return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
        atomic_store_explicit(&decoder->cancelled, false, memory_order_relaxed);
        input_frame = av_rescale((int64_t)frame_index, decoder->input_sample_rate,
                                 decoder->output_sample_rate);
        libvgmstream_seek(decoder->vgmstream, input_frame);
        swr_close(decoder->resampler);
        result = swr_init(decoder->resampler);
        if (result < 0) {
            decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, result,
                              "FFmpeg could not reset vgmstream conversion after seeking");
            return WADDAMBURO_MEDIA_ERROR_INTERNAL;
        }
        decoder->pending_frames = 0U;
        decoder->pending_offset = 0U;
        decoder->demux_eof = 0;
        decoder->decoder_eof = 0;
        decoder->resampler_flushed = 0;
        return WADDAMBURO_MEDIA_OK;
    }
#endif
    if (decoder->format->pb == NULL || decoder->format->pb->seekable == 0)
        return WADDAMBURO_MEDIA_ERROR_UNSUPPORTED;
    if (frame_index > INT64_MAX)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    atomic_store_explicit(&decoder->cancelled, false, memory_order_relaxed);
    stream = decoder->format->streams[decoder->audio_stream];
    timestamp = av_rescale_q((int64_t)frame_index,
                             (AVRational){1, (int)decoder->output_sample_rate}, stream->time_base);
    if (stream->start_time != AV_NOPTS_VALUE)
        timestamp += stream->start_time;
    result = avformat_seek_file(decoder->format, decoder->audio_stream, INT64_MIN, timestamp, timestamp, 0);
    if (result < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_IO, result,
                          "FFmpeg could not seek the audio input");
        return WADDAMBURO_MEDIA_ERROR_IO;
    }
    avcodec_flush_buffers(decoder->codec);
    swr_close(decoder->resampler);
    result = swr_init(decoder->resampler);
    if (result < 0) {
        decoder_set_error(decoder, WADDAMBURO_MEDIA_ERROR_INTERNAL, result,
                          "FFmpeg could not reset audio conversion after seeking");
        return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }
    decoder->pending_frames = 0U;
    decoder->pending_offset = 0U;
    decoder->demux_eof = 0;
    decoder->decoder_eof = 0;
    decoder->resampler_flushed = 0;
    return WADDAMBURO_MEDIA_OK;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_cancel(
    waddamburo_media_decoder *decoder)
{
    if (decoder == NULL)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    atomic_store_explicit(&decoder->cancelled, true, memory_order_relaxed);
    return WADDAMBURO_MEDIA_OK;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_get_error(
    const waddamburo_media_decoder *decoder,
    waddamburo_media_error *error)
{
    if (decoder == NULL || !error_is_valid(error))
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    *error = decoder->error;
    return WADDAMBURO_MEDIA_OK;
}

void WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_destroy(waddamburo_media_decoder *decoder)
{
    decoder_destroy_internal(decoder);
}

#else

struct waddamburo_media_decoder {
    int unused;
};

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
    const size_t base_size = offsetof(waddamburo_media_stream_info, total_frames) +
                             sizeof(stream_info->total_frames);
    if (decoder == NULL || stream_info == NULL || stream_info->struct_size < base_size)
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

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_seek(
    waddamburo_media_decoder *decoder,
    uint64_t frame_index)
{
    (void)frame_index;
    return decoder == NULL ? WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT : WADDAMBURO_MEDIA_ERROR_INTERNAL;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_decoder_cancel(
    waddamburo_media_decoder *decoder)
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

#endif
