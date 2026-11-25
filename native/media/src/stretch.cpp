// The time-stretcher: Bungee's granular API over a song held in memory (random access, so a restart anywhere is free).
#include "waddamburo/media.h"

#include <bungee/Bungee.h>

#include <algorithm>
#include <cmath>
#include <limits>
#include <new>
#include <vector>

struct waddamburo_media_stretch
{
    waddamburo_media_stretch(int32_t sampleRate, int32_t channelCount) :
        stretcher(Bungee::SampleRates{sampleRate, sampleRate}, channelCount),
        channels(channelCount),
        stride(stretcher.maxInputFrameCount()),
        input(static_cast<size_t>(stride) * static_cast<size_t>(channelCount))
    {
    }

    Bungee::Stretcher<Bungee::Basic> stretcher;
    int channels;
    int stride;
    Bungee::Request request{};
    std::vector<float> input; // one grain's input, channel after channel
    std::vector<float> pending; // rendered frames not handed out yet, interleaved
    std::vector<double> pendingPositions; // the song frame of each
    size_t taken = 0; // frames of pending already handed out
    double discardBefore = -std::numeric_limits<double>::infinity(); // preroll output before the restart point

    // One grain: its input from the song, its output (past the restart point) onto pending.
    void grain(const float *pcm, int64_t pcmFrames)
    {
        const Bungee::InputChunk chunk = stretcher.specifyGrain(request);
        const int length = chunk.end - chunk.begin;
        for (int channel = 0; channel < channels; ++channel)
        {
            float *destination = input.data() + static_cast<size_t>(channel) * static_cast<size_t>(stride);
            for (int i = 0; i < length; ++i)
            {
                const int64_t frame = static_cast<int64_t>(chunk.begin) + i;
                destination[i] = frame >= 0 && frame < pcmFrames ? pcm[frame * channels + channel] : 0.f;
            }
        }
        const int muteHead = std::clamp(-chunk.begin, 0, length);
        const int muteTail = static_cast<int>(std::clamp<int64_t>(chunk.end - pcmFrames, 0, length));
        stretcher.analyseGrain(input.data(), stride, muteHead, muteTail);

        Bungee::OutputChunk output{};
        stretcher.synthesiseGrain(output);
        if (output.frameCount > 0 && output.request[0] && output.request[1])
        {
            const double from = output.request[0]->position, to = output.request[1]->position;
            for (int i = 0; i < output.frameCount; ++i)
            {
                const double position = from + (to - from) * i / output.frameCount;
                if (std::isnan(position) || position < discardBefore)
                    continue;
                for (int channel = 0; channel < channels; ++channel)
                    pending.push_back(output.data[static_cast<intptr_t>(channel) * output.channelStride + i]);
                pendingPositions.push_back(position);
            }
        }
        stretcher.next(request);
    }
};

extern "C" {

waddamburo_media_stretch *waddamburo_media_stretch_create(int32_t sample_rate, int32_t channels)
{
    if (sample_rate <= 0 || channels <= 0)
        return nullptr;
    try
    {
        return new waddamburo_media_stretch(sample_rate, channels);
    }
    catch (...)
    {
        return nullptr;
    }
}

void waddamburo_media_stretch_destroy(waddamburo_media_stretch *stretch)
{
    delete stretch;
}

int32_t waddamburo_media_stretch_render(waddamburo_media_stretch *stretch, const float *pcm, int64_t pcm_frame_count,
    double speed, int32_t restart, double *position, float *output, int32_t frame_count)
{
    if (!stretch || !pcm || !position || !output || frame_count <= 0 || !(speed > 0))
        return 0;
    try
    {
        auto &s = *stretch;
        if (restart)
        {
            s.pending.clear();
            s.pendingPositions.clear();
            s.taken = 0;
            s.request = Bungee::Request{*position, speed, 1., true, resampleMode_autoOut};
            s.stretcher.preroll(s.request);
            s.discardBefore = *position;
        }
        s.request.speed = speed;
        // A grain may yield nothing (preroll, or a restart's first grains): give up after many in a row.
        for (int idle = 0; s.pendingPositions.size() - s.taken < static_cast<size_t>(frame_count); )
        {
            const size_t before = s.pendingPositions.size();
            s.grain(pcm, pcm_frame_count);
            if (s.pendingPositions.size() > before)
            {
                idle = 0;
                s.discardBefore = -std::numeric_limits<double>::infinity();
            }
            else if (++idle > 64)
                return 0;
        }
        std::copy_n(s.pending.begin() + static_cast<ptrdiff_t>(s.taken * s.channels),
            static_cast<size_t>(frame_count) * s.channels, output);
        s.taken += static_cast<size_t>(frame_count);
        *position = s.taken < s.pendingPositions.size() ? s.pendingPositions[s.taken]
            : s.pendingPositions[s.taken - 1] + speed;
        // Drop what was handed out once it is most of the buffer.
        if (s.taken > 4096 && s.taken * 2 > s.pendingPositions.size())
        {
            s.pending.erase(s.pending.begin(), s.pending.begin() + static_cast<ptrdiff_t>(s.taken * s.channels));
            s.pendingPositions.erase(s.pendingPositions.begin(), s.pendingPositions.begin() + static_cast<ptrdiff_t>(s.taken));
            s.taken = 0;
        }
        return frame_count;
    }
    catch (...)
    {
        return 0;
    }
}

}
