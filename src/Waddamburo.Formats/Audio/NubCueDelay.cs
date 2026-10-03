using System.Buffers.Binary;

namespace Waddamburo.Formats.Audio;

/// <summary>
/// A nuSound2 bank cue's start delay. NUB layout (big-endian): stream count at 0x0C, the stream
/// header offsets from 0x20; a "vag\0" stream header holds the delay in milliseconds at 0x4C.
/// Found on the asset files: only voices and effects that wait for an animation carry one
/// (VO_GAME's full-combo voices 1200 ms, the banner's text appearing ~1.3 s after its jump, while
/// the traced game requests the cue at the jump).
/// </summary>
public static class NubCueDelay
{
    private const int HeaderTable = 0x20;
    private const int DelayField = 0x4C;

    /// <summary>The cue's delay; zero for other stream kinds or a cue the bank lacks.</summary>
    public static TimeSpan Read(ReadOnlySpan<byte> bank, int cue)
    {
        if (bank.Length < HeaderTable || cue < 0)
            return TimeSpan.Zero;
        var count = BinaryPrimitives.ReadUInt32BigEndian(bank[0x0C..]);
        var entry = HeaderTable + (long)cue * 4;
        if ((uint)cue >= count || entry + 4 > bank.Length)
            return TimeSpan.Zero;
        var header = BinaryPrimitives.ReadUInt32BigEndian(bank[(int)entry..]);
        if (header + DelayField + 4L > bank.Length || !bank.Slice((int)header, 4).SequenceEqual("vag\0"u8))
            return TimeSpan.Zero;
        var delay = BinaryPrimitives.ReadUInt32BigEndian(bank[(int)(header + DelayField)..]);
        return delay > 10_000 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(delay);
    }
}
