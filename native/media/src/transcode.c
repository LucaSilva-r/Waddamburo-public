#include "waddamburo/media.h"

#include <stdio.h>
#include <string.h>

static void transcode_set_error(waddamburo_media_error *error, waddamburo_media_result code, int native_code,
    const char *message)
{
    if (error == NULL || error->struct_size < sizeof(*error))
        return;
    error->code = code;
    error->native_code = native_code;
    snprintf(error->message, sizeof(error->message), "%s", message);
    error->message_length = (uint32_t)strlen(error->message);
}

#if defined(WADDAMBURO_MEDIA_HAS_FFMPEG)
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
#include <libavutil/dict.h>

#define OPUS_RATE 48000

typedef struct transcode_state {
    waddamburo_media_decoder *decoder;
    AVCodecContext *codec;
    AVFormatContext *format;
    AVStream *stream;
    AVFrame *frame;
    AVPacket *packet;
    float *interleaved;
    int header_written;
} transcode_state;

static int write_packets(transcode_state *state)
{
    int result;
    while ((result = avcodec_receive_packet(state->codec, state->packet)) >= 0) {
        av_packet_rescale_ts(state->packet, state->codec->time_base, state->stream->time_base);
        state->packet->stream_index = state->stream->index;
        result = av_interleaved_write_frame(state->format, state->packet);
        if (result < 0)
            return result;
    }
    return result == AVERROR(EAGAIN) || result == AVERROR_EOF ? 0 : result;
}

static void transcode_free(transcode_state *state)
{
    if (state->format != NULL) {
        if (state->header_written)
            av_write_trailer(state->format);
        if (state->format->pb != NULL)
            avio_closep(&state->format->pb);
        avformat_free_context(state->format);
    }
    av_packet_free(&state->packet);
    av_frame_free(&state->frame);
    avcodec_free_context(&state->codec);
    av_free(state->interleaved);
    waddamburo_media_decoder_destroy(state->decoder);
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_transcode_opus(
    const char *utf8_input_path,
    const char *utf8_output_path,
    uint32_t bit_rate,
    waddamburo_media_error *error)
{
    if (utf8_input_path == NULL || utf8_output_path == NULL || bit_rate == 0U) {
        transcode_set_error(error, WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT, 0, "Invalid transcode arguments.");
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    }
    transcode_state state;
    memset(&state, 0, sizeof(state));
    const char *failure = "Opus encoding failed.";
    waddamburo_media_result code = WADDAMBURO_MEDIA_ERROR_INTERNAL;
    int native = 0;

    /* The input through the ordinary decoder, resampled to Opus's 48 kHz stereo. */
    waddamburo_media_decoder_options options;
    memset(&options, 0, sizeof(options));
    options.struct_size = sizeof(options);
    options.output_sample_rate = OPUS_RATE;
    options.output_channels = 2U;
    waddamburo_media_error decode_error;
    memset(&decode_error, 0, sizeof(decode_error));
    decode_error.struct_size = sizeof(decode_error);
    code = waddamburo_media_decoder_create_file(&options, utf8_input_path, &state.decoder, &decode_error);
    if (code != WADDAMBURO_MEDIA_OK) {
        transcode_set_error(error, code, decode_error.native_code, decode_error.message);
        return code;
    }
    code = WADDAMBURO_MEDIA_ERROR_INTERNAL;

    const AVCodec *encoder = avcodec_find_encoder_by_name("libopus");
    if (encoder == NULL) {
        failure = "This build has no Opus encoder.";
        code = WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE;
        goto fail;
    }
    state.codec = avcodec_alloc_context3(encoder);
    state.frame = av_frame_alloc();
    state.packet = av_packet_alloc();
    if (state.codec == NULL || state.frame == NULL || state.packet == NULL)
        goto fail;
    state.codec->sample_rate = OPUS_RATE;
    state.codec->sample_fmt = AV_SAMPLE_FMT_FLT; /* libopus takes interleaved float */
    state.codec->bit_rate = bit_rate;
    state.codec->time_base = (AVRational){1, OPUS_RATE};
    av_channel_layout_default(&state.codec->ch_layout, 2);

    if ((native = avformat_alloc_output_context2(&state.format, NULL, "ogg", utf8_output_path)) < 0)
        goto fail;
    if (state.format->oformat->flags & AVFMT_GLOBALHEADER)
        state.codec->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    {
        /* The encoder buffers one sent frame per 2.5 ms of opus_delay in a 64-frame queue; the
           default delay (157.5 ms) overflows it with 20 ms frames and drops audio. */
        AVDictionary *encoder_options = NULL;
        av_dict_set(&encoder_options, "opus_delay", "20", 0);
        native = avcodec_open2(state.codec, encoder, &encoder_options);
        av_dict_free(&encoder_options);
        if (native < 0)
            goto fail;
    }
    state.stream = avformat_new_stream(state.format, NULL);
    if (state.stream == NULL)
        goto fail;
    state.stream->time_base = state.codec->time_base;
    if ((native = avcodec_parameters_from_context(state.stream->codecpar, state.codec)) < 0)
        goto fail;
    if ((native = avio_open(&state.format->pb, utf8_output_path, AVIO_FLAG_WRITE)) < 0) {
        code = WADDAMBURO_MEDIA_ERROR_IO;
        failure = "Cannot write the Opus file.";
        goto fail;
    }
    if ((native = avformat_write_header(state.format, NULL)) < 0)
        goto fail;
    state.header_written = 1;

    const int frame_size = state.codec->frame_size > 0 ? state.codec->frame_size : 960;
    state.interleaved = av_malloc(sizeof(float) * 2U * (size_t)frame_size);
    if (state.interleaved == NULL)
        goto fail;
    int64_t pts = 0;
    for (;;) {
        uint64_t frames = 0;
        const waddamburo_media_result read = waddamburo_media_decoder_read_frames(
            state.decoder, state.interleaved, (uint64_t)frame_size, &frames);
        if (read != WADDAMBURO_MEDIA_OK && read != WADDAMBURO_MEDIA_END_OF_STREAM) {
            code = read;
            failure = "Decoding the input failed.";
            goto fail;
        }
        if (frames > 0U) {
            av_frame_unref(state.frame);
            state.frame->nb_samples = frame_size;
            state.frame->format = state.codec->sample_fmt;
            state.frame->sample_rate = OPUS_RATE;
            if ((native = av_channel_layout_copy(&state.frame->ch_layout, &state.codec->ch_layout)) < 0
                || (native = av_frame_get_buffer(state.frame, 0)) < 0)
                goto fail;
            /* A short last frame is padded with silence (the encoder takes whole frames). */
            float *samples = (float *)state.frame->data[0];
            memcpy(samples, state.interleaved, sizeof(float) * 2U * (size_t)frames);
            memset(samples + 2U * frames, 0, sizeof(float) * 2U * ((size_t)frame_size - (size_t)frames));
            state.frame->pts = pts;
            pts += frame_size;
            if ((native = avcodec_send_frame(state.codec, state.frame)) < 0 || (native = write_packets(&state)) < 0)
                goto fail;
        }
        if (read == WADDAMBURO_MEDIA_END_OF_STREAM || frames < (uint64_t)frame_size)
            break;
    }
    if ((native = avcodec_send_frame(state.codec, NULL)) < 0 || (native = write_packets(&state)) < 0)
        goto fail;
    transcode_free(&state);
    return WADDAMBURO_MEDIA_OK;

fail:
    {
        char message[256];
        if (native < 0) {
            char reason[AV_ERROR_MAX_STRING_SIZE];
            av_strerror(native, reason, sizeof(reason));
            snprintf(message, sizeof(message), "%s (%s)", failure, reason);
        } else {
            snprintf(message, sizeof(message), "%s", failure);
        }
        transcode_set_error(error, code, native, message);
    }
    transcode_free(&state);
    return code;
}
#else
waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_transcode_opus(
    const char *utf8_input_path,
    const char *utf8_output_path,
    uint32_t bit_rate,
    waddamburo_media_error *error)
{
    (void)utf8_input_path;
    (void)utf8_output_path;
    (void)bit_rate;
    transcode_set_error(error, WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE, 0, "The media library was built without FFmpeg.");
    return WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE;
}
#endif
