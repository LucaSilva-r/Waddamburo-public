using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.Architecture.Tests;

public sealed class AudioPlaybackClockTests
{
    [Theory]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(240)]
    public void BufferProgressProducesContinuousDisplayRateMotion(int refreshRate)
    {
        var clock = new AudioPlaybackClock();
        var latency = TimeSpan.FromSeconds(1024d / 48000);
        var previous = clock.Update(TimeSpan.Zero, latency, TimeSpan.Zero);
        for (var frame = 1; frame <= refreshRate * 10; frame++)
        {
            var now = TimeSpan.FromSeconds((double)frame / refreshRate);
            var consumed = TimeSpan.FromSeconds(Math.Floor(now.TotalSeconds / latency.TotalSeconds)
                * latency.TotalSeconds);
            var position = clock.Update(consumed, latency, now);
            if (now > latency * 3)
                Assert.InRange((position - previous).TotalSeconds, 0.99 / refreshRate - 0.000001,
                    1.01 / refreshRate + 0.000001);
            Assert.InRange(position.TotalSeconds, Math.Max(0, now.TotalSeconds - latency.TotalSeconds * 3),
                now.TotalSeconds);
            previous = position;
        }
    }

    [Fact]
    public void StalledAudioBoundsExtrapolationAndRecoveryNeverRewinds()
    {
        var clock = new AudioPlaybackClock();
        var latency = TimeSpan.FromMilliseconds(20);
        Assert.Equal(TimeSpan.Zero, clock.Update(TimeSpan.Zero, latency, TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, clock.Update(TimeSpan.Zero, latency, TimeSpan.FromSeconds(1)));
        var consumed = TimeSpan.FromMilliseconds(100);
        var before = clock.Update(consumed, latency, TimeSpan.FromMilliseconds(1100));
        var stalled = clock.Update(consumed, latency, TimeSpan.FromSeconds(2));
        Assert.Equal(before, stalled);
        Assert.Equal(stalled, clock.Update(consumed, latency, TimeSpan.FromSeconds(3)));
        Assert.True(clock.Update(TimeSpan.FromMilliseconds(120), latency, TimeSpan.FromMilliseconds(3005)) >= stalled);
    }

    [Fact]
    public void IrregularBufferConsumptionDoesNotCreatePositionJumps()
    {
        var clock = new AudioPlaybackClock();
        var latency = TimeSpan.FromMilliseconds(20);
        var consumed = TimeSpan.FromSeconds(1);
        var previous = clock.Update(consumed, latency, TimeSpan.Zero);
        for (var frame = 1; frame <= 2400; frame++)
        {
            var now = TimeSpan.FromSeconds(frame / 240d);
            // Audio delivery alternates between long gaps and larger catch-up batches.
            if (frame % 17 == 0)
                consumed = TimeSpan.FromSeconds(1 + now.TotalSeconds);
            var position = clock.Update(consumed, latency, now);
            // Gaps longer than the latency put it more than a buffer off: it corrects at up to 5%, still no jump.
            Assert.InRange((position - previous).TotalSeconds, 0.95 / 240 - 0.000001,
                1.05 / 240 + 0.000001);
            previous = position;
        }
    }

    [Fact]
    public void ExclusivePeriodSlipIsCaughtQuicklyWithoutSteps()
    {
        // WASAPI exclusive: 3.33 ms periods; one 10 ms stall puts the audio 10 ms behind the wall clock for good.
        var clock = new AudioPlaybackClock();
        var period = TimeSpan.FromSeconds(160d / 48000);
        var stall = TimeSpan.FromSeconds(0.5);
        var slip = TimeSpan.FromMilliseconds(10);
        var previous = clock.Update(TimeSpan.Zero, period, TimeSpan.Zero);
        for (var frame = 1; frame <= 600; frame++)
        {
            var now = TimeSpan.FromSeconds(frame / 600d);
            // The device thread stalls at 0.5 s for 10 ms (the audio stands still), then plays on that much behind.
            var audio = now < stall ? now : now < stall + slip ? stall : now - slip;
            var consumed = TimeSpan.FromSeconds(Math.Floor(audio.TotalSeconds / period.TotalSeconds) * period.TotalSeconds);
            var position = clock.Update(consumed, period, now);
            if (now > period * 3)
                Assert.InRange((position - previous).TotalSeconds, 0.95 / 600 - 0.000001, 1.05 / 600 + 0.000001);
            previous = position;
        }
        // 0.5 s after the slip it is back within a period of the audio, not still 10 ms ahead (1% took a second).
        Assert.InRange((previous - (TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(10))).TotalMilliseconds, -4, 4);
    }
}
