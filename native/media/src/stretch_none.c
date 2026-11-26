/* The time-stretcher's entry points for builds without Bungee (the Switch): nothing can be made, so callers play
   resampled. The full build compiles stretch.cpp instead. */
#include "waddamburo/media.h"

#include <stddef.h>

waddamburo_media_stretch *WADDAMBURO_MEDIA_CALL waddamburo_media_stretch_create(int32_t sample_rate, int32_t channels)
{
    (void)sample_rate;
    (void)channels;
    return NULL;
}

void WADDAMBURO_MEDIA_CALL waddamburo_media_stretch_destroy(waddamburo_media_stretch *stretch)
{
    (void)stretch;
}

int32_t WADDAMBURO_MEDIA_CALL waddamburo_media_stretch_render(waddamburo_media_stretch *stretch, const float *pcm,
    int64_t pcm_frame_count, double speed, int32_t restart, double *position, float *output, int32_t frame_count)
{
    (void)stretch, (void)pcm, (void)pcm_frame_count, (void)speed, (void)restart, (void)position, (void)output;
    (void)frame_count;
    return 0;
}
