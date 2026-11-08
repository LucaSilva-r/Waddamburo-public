using Waddamburo.Formats.Audio;

namespace Waddamburo.Formats.Tests;

public sealed class LoudnessMeterTests
{
    // BS.1770's reference: a 997 Hz sine at -20 dBFS in both channels of a stereo signal reads -20 LUFS
    // (K-weighting is ~0 dB there and the channels add up); fed in odd chunks, at 44.1 and 48 kHz.
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void ReferenceSineReadsMinusTwenty(int rate)
    {
        var meter = new LoudnessMeter(rate, 2);
        const double amplitude = 0.1; // -20 dBFS peak
        var pcm = new float[rate * 2 * 5];
        for (var frame = 0; frame < pcm.Length / 2; frame++)
            pcm[frame * 2] = pcm[frame * 2 + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * 997 * frame / rate));
        for (var offset = 0; offset < pcm.Length; offset += 1234)
            meter.Add(pcm.AsSpan(offset, Math.Min(1234, pcm.Length - offset)));

        Assert.InRange(meter.Integrated, -20.2, -19.8);
    }

    [Fact]
    public void SilenceIsNegativeInfinity()
    {
        var meter = new LoudnessMeter(48000, 2);
        meter.Add(new float[48000 * 2]);
        Assert.Equal(double.NegativeInfinity, meter.Integrated);
    }
}
