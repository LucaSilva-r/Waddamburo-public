using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.App.Tests;

public sealed class ScrubAudioSourceTests
{
    private sealed class FakeTime : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1_000_000;
        public override long GetTimestamp() => Now;
    }

    // Autoplay: the playhead moves at 1x with the wall clock, the mixer renders a whole queue refill at once and then
    // idles. The sound must play at an even 1x, not speed up and down with each burst.
    [Fact]
    public void BurstyRenderingKeepsAnEvenRate()
    {
        const int rate = 48000, frames = rate * 6;
        var pcm = new float[frames]; // mono ramp: a sample's value is its own position
        for (var i = 0; i < frames; i++)
            pcm[i] = (float)i / frames;
        var time = new FakeTime();
        var source = ScrubAudioSource.FromPcm(new SdlAudioFormat(rate, 1), pcm, time);
        var block = new float[128];
        var worst = 0.0;
        long nextFrame = 0, nextBurst = 0;
        for (long micros = 0; micros < 4_000_000; micros += 1000)
        {
            time.Now = micros;
            if (micros >= nextFrame) // the game reports the playhead at 60 Hz
            {
                source.Follow(TimeSpan.FromTicks(micros * 10), 1);
                nextFrame += 16_667;
            }
            if (micros < nextBurst)
                continue;
            nextBurst += 21_333; // 1024 frames at once
            for (var b = 0; b < 8; b++)
            {
                source.Read(block);
                var step = (block[^1] - block[1]) * frames / (block.Length - 2);
                if (micros > 1_500_000)
                    worst = Math.Max(worst, Math.Abs(step - 1));
            }
        }
        Assert.True(worst < 0.001, $"rate strayed by {worst:P2}");
        // And it stays with the playhead: the last block is within 50 ms of the 4 s mark.
        Assert.InRange(block[^1] * frames / rate, 3.95, 4.05);
    }

    // The pause menu: the playhead stands still for a second, then plays on. The sound resumes at 1x, not pitched up
    // to catch up.
    [Fact]
    public void ResumingAfterAPausePlaysAtTheRate()
    {
        const int rate = 48000, frames = rate * 6;
        var pcm = new float[frames];
        for (var i = 0; i < frames; i++)
            pcm[i] = (float)i / frames;
        var time = new FakeTime();
        var source = ScrubAudioSource.FromPcm(new SdlAudioFormat(rate, 1), pcm, time);
        var block = new float[128];
        var worst = 0.0;
        var position = TimeSpan.Zero;
        for (long micros = 0; micros < 4_000_000; micros += 2667) // one 128-frame block per step
        {
            var paused = micros is > 1_000_000 and < 2_000_000;
            time.Now = micros;
            if (!paused)
                position += TimeSpan.FromTicks(26_670);
            source.Follow(position, paused ? 0 : 1);
            source.Read(block);
            var step = (block[^1] - block[1]) * frames / (block.Length - 2);
            if (micros > 2_100_000) // past the 5 ms fade-in
                worst = Math.Max(worst, Math.Abs(step - 1));
        }
        Assert.True(worst < 0.001, $"rate strayed by {worst:P2} after the pause");
    }

    // Training mode: through the time-stretcher a 1 kHz tone stays at 1 kHz at any steady speed (resampled at 0.5x it
    // would drop to 500 Hz), in every 100 ms, including across a speed change. Measured only where the native library
    // carries the stretcher (release libraries).
    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.05, 0.05)]
    [InlineData(0.5, 0.75)]
    public void SteadySpeedsKeepThePitch(double speed, double laterSpeed)
    {
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("waddamburo_media", typeof(ScrubAudioSource).Assembly, null, out var library)
            || !System.Runtime.InteropServices.NativeLibrary.TryGetExport(library, "waddamburo_media_stretch_render", out _))
            return;
        const int rate = 48000, frames = rate * 6;
        var pcm = new float[frames];
        for (var i = 0; i < frames; i++)
            pcm[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / rate);
        var time = new FakeTime();
        var source = ScrubAudioSource.FromPcm(new SdlAudioFormat(rate, 1), pcm, time);
        var block = new float[480];
        var (crossings, counted, previous) = (0, 0, 0f);
        var position = TimeSpan.Zero;
        for (long micros = 0; micros < 4_000_000; micros += 10_000) // one 10 ms block per step
        {
            time.Now = micros;
            var now = micros < 2_000_000 ? speed : laterSpeed;
            position += TimeSpan.FromTicks((long)(100_000 * now)); // 10 ms at that speed, in whole ticks
            // The game's rate jitters in the last digits (distance over time, in 100 ns ticks).
            source.Follow(position, now * (1 + ((micros / 10_000) % 3 - 1) * 1e-7), keepPitch: true);
            source.Read(block);
            if (micros < 500_000)
                continue;
            foreach (var sample in block)
            {
                if (previous < 0 && sample >= 0)
                    crossings++;
                previous = sample;
            }
            if ((counted += block.Length) < rate / 10)
                continue;
            Assert.InRange(crossings * (double)rate / counted, 900, 1100);
            (crossings, counted) = (0, 0);
        }
    }
}
