#include "waddamburo/media.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(WADDAMBURO_MEDIA_HAS_FFMPEG)
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/error.h>
#include <libavutil/imgutils.h>
#include <libswscale/swscale.h>


struct waddamburo_media_video {
    AVFormatContext *format;
    AVCodecContext *codec;
    AVPacket *packet;
    AVFrame *frame;
    struct SwsContext *scaler;
    int stream_index;
    int draining;
    int64_t start_pts;
    int64_t skip_until_microseconds;
    AVRational time_base;
    uint32_t width;
    uint32_t height;
};

static void video_set_error(waddamburo_media_error *error, waddamburo_media_result code, int native_code,
    const char *message)
{
    if (error == NULL || error->struct_size < sizeof(*error))
        return;
    error->code = code;
    error->native_code = native_code;
    snprintf(error->message, sizeof(error->message), "%s", message);
    error->message_length = (uint32_t)strlen(error->message);
}

void WADDAMBURO_MEDIA_CALL waddamburo_media_video_destroy(waddamburo_media_video *video)
{
    if (video == NULL)
        return;
    sws_freeContext(video->scaler);
    av_frame_free(&video->frame);
    av_packet_free(&video->packet);
    avcodec_free_context(&video->codec);
    avformat_close_input(&video->format);
    free(video);
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_video_open_file(
    const char *utf8_path,
    waddamburo_media_video **video_out,
    waddamburo_media_video_info *info,
    waddamburo_media_error *error)
{
    if (utf8_path == NULL || video_out == NULL || info == NULL || info->struct_size < sizeof(*info)) {
        video_set_error(error, WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT, 0, "Invalid video open arguments.");
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    }
    *video_out = NULL;
    waddamburo_media_video *video = calloc(1, sizeof(*video));
    if (video == NULL) {
        video_set_error(error, WADDAMBURO_MEDIA_ERROR_INTERNAL, 0, "Out of memory.");
        return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }

    /* PAMF (PS3 movies) is an MPEG program stream behind a sector-aligned header the
       probe does not recognise; the program-stream demuxer resynchronises past it. Anything else is probed. */
    size_t path_length = strlen(utf8_path);
    int pamf = path_length >= 4 && (strcmp(utf8_path + path_length - 4, ".pam") == 0
        || strcmp(utf8_path + path_length - 4, ".PAM") == 0);
    const AVInputFormat *program_stream = pamf ? av_find_input_format("mpeg") : NULL;
    int status = avformat_open_input(&video->format, utf8_path, program_stream, NULL);
    if (status < 0)
        goto ffmpeg_failure;
    status = avformat_find_stream_info(video->format, NULL);
    if (status < 0)
        goto ffmpeg_failure;
    const AVCodec *decoder = NULL;
    status = av_find_best_stream(video->format, AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
    if (status < 0)
        goto ffmpeg_failure;
    video->stream_index = status;
    AVStream *stream = video->format->streams[video->stream_index];
    video->codec = avcodec_alloc_context3(decoder);
    if (video->codec == NULL) {
        status = AVERROR(ENOMEM);
        goto ffmpeg_failure;
    }
    status = avcodec_parameters_to_context(video->codec, stream->codecpar);
    if (status < 0)
        goto ffmpeg_failure;
    /* Every core (FFmpeg picks the count): one thread could not keep a 1080p60 background video up with the game
       running (84 frames a second alone, without SIMD). */
#if defined(__SWITCH__)
    /* FFmpeg built for no OS cannot count cores there (it would take one): the three the game runs on. */
    video->codec->thread_count = 3;
#else
    video->codec->thread_count = 0;
#endif
    video->codec->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
    status = avcodec_open2(video->codec, decoder, NULL);
    if (status < 0)
        goto ffmpeg_failure;

    video->packet = av_packet_alloc();
    video->frame = av_frame_alloc();
    if (video->packet == NULL || video->frame == NULL) {
        status = AVERROR(ENOMEM);
        goto ffmpeg_failure;
    }
    video->time_base = stream->time_base;
    video->skip_until_microseconds = INT64_MIN;
    video->start_pts = stream->start_time == AV_NOPTS_VALUE ? 0 : stream->start_time;
    video->width = (uint32_t)video->codec->width;
    video->height = (uint32_t)video->codec->height;
    /* Frames come out at most 720 lines high (even sizes): a 1080p background is converted and uploaded
       at the stage's size instead of 2.25 times it. */
    if (video->height > 720U && video->width > 0U) {
        video->width = (uint32_t)(((uint64_t)video->width * 720U / video->height + 1U) & ~1U);
        video->height = 720U;
    }
    if (video->width == 0 || video->height == 0) {
        status = AVERROR_INVALIDDATA;
        goto ffmpeg_failure;
    }
    info->width = video->width;
    info->height = video->height;
    info->duration_microseconds = video->format->duration == AV_NOPTS_VALUE ? -1 : video->format->duration;
    *video_out = video;
    return WADDAMBURO_MEDIA_OK;

ffmpeg_failure: {
        char message[128];
        av_strerror(status, message, sizeof(message));
        video_set_error(error, WADDAMBURO_MEDIA_ERROR_UNSUPPORTED, status, message);
        waddamburo_media_video_destroy(video);
        return WADDAMBURO_MEDIA_ERROR_UNSUPPORTED;
    }
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_video_read_frame(
    waddamburo_media_video *video,
    uint8_t *rgba,
    uint64_t capacity,
    int64_t *pts_microseconds)
{
    if (video == NULL || rgba == NULL || pts_microseconds == NULL
        || capacity < (uint64_t)video->width * video->height * 4U)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;

next_frame:
    for (;;) {
        int status = avcodec_receive_frame(video->codec, video->frame);
        if (status == 0)
            break;
        if (status == AVERROR_EOF)
            return WADDAMBURO_MEDIA_END_OF_STREAM;
        if (status != AVERROR(EAGAIN))
            return WADDAMBURO_MEDIA_ERROR_INTERNAL;
        if (video->draining)
            return WADDAMBURO_MEDIA_END_OF_STREAM;
        status = av_read_frame(video->format, video->packet);
        if (status == AVERROR_EOF) {
            video->draining = 1;
            avcodec_send_packet(video->codec, NULL);
            continue;
        }
        if (status < 0)
            return WADDAMBURO_MEDIA_ERROR_IO;
        if (video->packet->stream_index == video->stream_index)
            status = avcodec_send_packet(video->codec, video->packet);
        av_packet_unref(video->packet);
        if (status < 0 && status != AVERROR(EAGAIN) && status != AVERROR_INVALIDDATA)
            return WADDAMBURO_MEDIA_ERROR_INTERNAL;
    }

    AVFrame *frame = video->frame;
    int64_t frame_pts = frame->best_effort_timestamp == AV_NOPTS_VALUE ? video->start_pts : frame->best_effort_timestamp;
    int64_t frame_microseconds = av_rescale_q(frame_pts - video->start_pts, video->time_base, AV_TIME_BASE_Q);
    /* After a seek: the frames between the keyframe and the time asked for are decoded, not converted. */
    if (frame_microseconds < video->skip_until_microseconds) {
        av_frame_unref(frame);
        goto next_frame;
    }
    video->skip_until_microseconds = INT64_MIN;
    video->scaler = sws_getCachedContext(video->scaler, frame->width, frame->height, frame->format,
        (int)video->width, (int)video->height, AV_PIX_FMT_RGBA, SWS_BILINEAR, NULL, NULL, NULL);
    if (video->scaler == NULL) {
        av_frame_unref(frame);
        return WADDAMBURO_MEDIA_ERROR_UNSUPPORTED;
    }
    const int *coefficients = sws_getCoefficients(
        frame->colorspace == AVCOL_SPC_BT470BG || frame->colorspace == AVCOL_SPC_SMPTE170M
            ? SWS_CS_ITU601 : SWS_CS_ITU709);
    sws_setColorspaceDetails(video->scaler, coefficients, frame->color_range == AVCOL_RANGE_JPEG,
        sws_getCoefficients(SWS_CS_DEFAULT), 1, 0, 1 << 16, 1 << 16);
    uint8_t *destination[4] = { rgba, NULL, NULL, NULL };
    int stride[4] = { (int)video->width * 4, 0, 0, 0 };
    sws_scale(video->scaler, (const uint8_t *const *)frame->data, frame->linesize, 0, frame->height,
        destination, stride);
    *pts_microseconds = frame_microseconds;
    av_frame_unref(frame);
    return WADDAMBURO_MEDIA_OK;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_video_seek(waddamburo_media_video *video,
    int64_t microseconds)
{
    if (video == NULL)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    if (microseconds < 0)
        microseconds = 0;
    int64_t target = video->start_pts + av_rescale_q(microseconds, AV_TIME_BASE_Q, video->time_base);
    if (av_seek_frame(video->format, video->stream_index, target, AVSEEK_FLAG_BACKWARD) < 0
        && av_seek_frame(video->format, -1, microseconds, AVSEEK_FLAG_BACKWARD) < 0)
        return WADDAMBURO_MEDIA_ERROR_IO;
    avcodec_flush_buffers(video->codec);
    video->draining = 0;
    video->skip_until_microseconds = microseconds;
    return WADDAMBURO_MEDIA_OK;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_video_limit_height(waddamburo_media_video *video,
    uint32_t height, uint32_t *width_out, uint32_t *height_out)
{
    if (video == NULL || width_out == NULL || height_out == NULL || height < 2)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    if (video->height > height) {
        video->width = (uint32_t)(((uint64_t)video->width * height / video->height + 1U) & ~1U);
        video->height = height & ~1U;
    }
    *width_out = video->width;
    *height_out = video->height;
    return WADDAMBURO_MEDIA_OK;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_video_skip_unreferenced(waddamburo_media_video *video,
    int32_t skip)
{
    if (video == NULL)
        return WADDAMBURO_MEDIA_ERROR_INVALID_ARGUMENT;
    video->codec->skip_frame = skip ? AVDISCARD_NONREF : AVDISCARD_DEFAULT;
    return WADDAMBURO_MEDIA_OK;
}
#else
waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_video_open_file(
    const char *utf8_path,
    waddamburo_media_video **video_out,
    waddamburo_media_video_info *info,
    waddamburo_media_error *error)
{
    (void)utf8_path;
    (void)info;
    if (video_out != NULL)
        *video_out = NULL;
    if (error != NULL && error->struct_size >= sizeof(*error)) {
        error->code = WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE;
        snprintf(error->message, sizeof(error->message), "Video decoding requires the FFmpeg backend.");
        error->message_length = (uint32_t)strlen(error->message);
    }
    return WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_video_read_frame(
    waddamburo_media_video *video,
    uint8_t *rgba,
    uint64_t capacity,
    int64_t *pts_microseconds)
{
    (void)video;
    (void)rgba;
    (void)capacity;
    (void)pts_microseconds;
    return WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE;
}

void WADDAMBURO_MEDIA_CALL waddamburo_media_video_destroy(waddamburo_media_video *video)
{
    (void)video;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_video_seek(waddamburo_media_video *video,
    int64_t microseconds)
{
    (void)video;
    (void)microseconds;
    return WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_video_limit_height(waddamburo_media_video *video,
    uint32_t height, uint32_t *width_out, uint32_t *height_out)
{
    (void)video;
    (void)height;
    (void)width_out;
    (void)height_out;
    return WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE;
}

waddamburo_media_result WADDAMBURO_MEDIA_CALL waddamburo_media_video_skip_unreferenced(waddamburo_media_video *video,
    int32_t skip)
{
    (void)video;
    (void)skip;
    return WADDAMBURO_MEDIA_ERROR_BACKEND_UNAVAILABLE;
}
#endif
