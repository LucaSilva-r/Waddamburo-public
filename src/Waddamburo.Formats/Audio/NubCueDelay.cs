using System.Buffers.Binary;

namespace Waddamburo.Formats.Audio;

/// <summary>
/// A nuSound2 bank cue's start delay. NUB layout (big-endian): stream count at 0x0C, the stream
/// header offsets from 0x20; a "vag\0" stream header holds the delay in milliseconds at 0x4C.
/// Found on the asset files: only voices and effects that wait for an animation carry one
/// (VO_GAME's full-combo voices 1200 ms, the banner's text appearing ~1.3 s after its jump, while
/// the traced game requests the cue at the jump). The same header holds the cue's authored gain.
/// </summary>
public static class NubCueDelay
{
    private const int HeaderTable = 0x20;
    private const int DelayField = 0x4C;
    private const int GainField = 0x34;
    private const int PitchField = 0x3C;
    private const int PanField = 0x40;

    /// <summary>
    /// The cue's pan in degrees, -90 (left) to 90 (right): a big-endian float at 0x40. The two-player drum banks
    /// SE_GAME_NEIRO_*_L/_R hold the _C bank's audio unchanged, panned -90 / 90 here; other cues use ±30 to ±75.
    /// 0 for other stream kinds, a missing cue or an implausible value.
    /// </summary>
    public static float ReadPan(ReadOnlySpan<byte> bank, int cue)
    {
        if (vagHeader(bank, cue) is not { } header)
            return 0;
        var degrees = BinaryPrimitives.ReadSingleBigEndian(bank[(header + PanField)..]);
        return float.IsFinite(degrees) && degrees is >= -90 and <= 90 ? degrees : 0;
    }

    /// <summary>
    /// The cue's pitch in cents: a big-endian int at 0x3C. The right drum bank (SE_GAME_NEIRO_*_R) holds -100, a
    /// semitone below the left one, so two players' drums sound apart. 0 for other stream kinds, a missing cue or an
    /// implausible value.
    /// </summary>
    public static int ReadPitch(ReadOnlySpan<byte> bank, int cue)
    {
        if (vagHeader(bank, cue) is not { } header)
            return 0;
        var cents = BinaryPrimitives.ReadInt32BigEndian(bank[(header + PitchField)..]);
        return cents is >= -2400 and <= 2400 ? cents : 0;
    }

    /// <summary>The cue's delay; zero for other stream kinds or a cue the bank lacks.</summary>
    public static TimeSpan Read(ReadOnlySpan<byte> bank, int cue)
    {
        if (vagHeader(bank, cue) is not { } header)
            return TimeSpan.Zero;
        var delay = BinaryPrimitives.ReadUInt32BigEndian(bank[(header + DelayField)..]);
        return delay > 10_000 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(delay);
    }

    /// <summary>
    /// The cue's authored gain as an amplitude factor (a big-endian float in dB at 0x34: Green's
    /// default drum Don -3.5, Ka -4.5); 1 for other stream kinds, a missing cue or an implausible value.
    /// A song's "at3\0" stream (SONG_*.nub) holds it at the same place (0 to -3.5 dB across Green's songs).
    /// </summary>
    public static float ReadGain(ReadOnlySpan<byte> bank, int cue)
    {
        if (vagHeader(bank, cue, at3: true) is not { } header)
            return 1f;
        var decibels = BinaryPrimitives.ReadSingleBigEndian(bank[(header + GainField)..]);
        return float.IsFinite(decibels) && decibels is >= -100 and <= 24 ? MathF.Pow(10, decibels / 20) : 1f;
    }

    // The offset of the cue's "vag\0" stream header, null when there is none.
    private static int? vagHeader(ReadOnlySpan<byte> bank, int cue, bool at3 = false)
    {
        if (bank.Length < HeaderTable || cue < 0)
            return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(bank[0x0C..]);
        var entry = HeaderTable + (long)cue * 4;
        if ((uint)cue >= count || entry + 4 > bank.Length)
            return null;
        var header = BinaryPrimitives.ReadUInt32BigEndian(bank[(int)entry..]);
        if (header + DelayField + 4L > bank.Length || !(bank.Slice((int)header, 4).SequenceEqual("vag\0"u8)
            || at3 && bank.Slice((int)header, 4).SequenceEqual("at3\0"u8)))
            return null;
        return (int)header;
    }
}
