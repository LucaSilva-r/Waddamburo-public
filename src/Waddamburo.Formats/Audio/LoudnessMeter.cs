namespace Waddamburo.Formats.Audio;

/// <summary>
/// ITU-R BS.1770-4 integrated loudness (LUFS, as ffmpeg's ebur128 reports it) of interleaved float PCM fed in
/// whole frames: K-weighting, 400 ms blocks every 100 ms, absolute gate -70 LUFS, relative gate -10 LU.
/// Every channel weighs 1 (ponytail: surround channels' 1.41 weight skipped, songs are stereo).
/// </summary>
public sealed class LoudnessMeter
{
    private readonly int _channels;
    private readonly int _step;
    private readonly double[] _state;
    private readonly double _b0, _b1, _b2, _a1, _a2, _c1, _c2;
    private readonly List<double> _steps = []; // each 100 ms step's mean square, summed over channels
    private double _sum;
    private int _count;

    public LoudnessMeter(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        _channels = channels;
        _step = sampleRate / 10;
        _state = new double[channels * 4];
        // The filters at any rate (libebur128's derivation of the standard's 48 kHz coefficients).
        var k = Math.Tan(Math.PI * 1681.974450955533 / sampleRate);
        var q = 0.7071752369554196;
        var vh = Math.Pow(10, 3.999843853973347 / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / q + k * k;
        (_b0, _b1, _b2) = ((vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0);
        (_a1, _a2) = (2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        k = Math.Tan(Math.PI * 38.13547087602444 / sampleRate);
        q = 0.5003270373238773;
        a0 = 1 + k / q + k * k;
        (_c1, _c2) = (2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0); // high-pass numerator 1, -2, 1
    }

    public void Add(ReadOnlySpan<float> interleaved)
    {
        for (var i = 0; i + _channels <= interleaved.Length; i += _channels)
        {
            double frame = 0;
            for (var c = 0; c < _channels; c++)
            {
                var s = c * 4;
                double x = interleaved[i + c];
                var y = _b0 * x + _state[s];
                _state[s] = _b1 * x - _a1 * y + _state[s + 1];
                _state[s + 1] = _b2 * x - _a2 * y;
                var z = y + _state[s + 2];
                _state[s + 2] = -2 * y - _c1 * z + _state[s + 3];
                _state[s + 3] = y - _c2 * z;
                frame += z * z;
            }
            _sum += frame;
            if (++_count == _step)
            {
                _steps.Add(_sum / _step);
                (_sum, _count) = (0, 0);
            }
        }
    }

    /// <summary>The integrated loudness so far; negative infinity for silence or under 400 ms.</summary>
    public double Integrated
    {
        get
        {
            var blocks = new List<double>();
            for (var i = 3; i < _steps.Count; i++)
                blocks.Add((_steps[i - 3] + _steps[i - 2] + _steps[i - 1] + _steps[i]) / 4);
            var loud = blocks.Where(static energy => lufs(energy) > -70).ToList();
            if (loud.Count == 0)
                return double.NegativeInfinity;
            var gate = lufs(loud.Average()) - 10;
            return lufs(loud.Where(energy => lufs(energy) > gate).Average());
        }
    }

    private static double lufs(double energy) => -0.691 + 10 * Math.Log10(energy);
}
