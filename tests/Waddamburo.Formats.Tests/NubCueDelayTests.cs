using System.Buffers.Binary;
using Waddamburo.Formats.Audio;

namespace Waddamburo.Formats.Tests;

public sealed class NubCueDelayTests
{
    [Fact]
    public void ReadsTheVagStreamDelayInMilliseconds()
    {
        // Two streams: cue 0 a vag with 1200 ms, cue 1 another kind with the same field set.
        var bank = new byte[0x200];
        BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(0x0C), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(0x24), 0x100);
        "vag\0"u8.CopyTo(bank.AsSpan(0x40));
        BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(0x40 + 0x4C), 1200);
        "wav\0"u8.CopyTo(bank.AsSpan(0x100));
        BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(0x100 + 0x4C), 5);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), NubCueDelay.Read(bank, 0));
        Assert.Equal(TimeSpan.Zero, NubCueDelay.Read(bank, 1));
        Assert.Equal(TimeSpan.Zero, NubCueDelay.Read(bank, 2));
        Assert.Equal(TimeSpan.Zero, NubCueDelay.Read(bank.AsSpan(0, 0x50), 0)); // truncated header
    }

    [Fact]
    public void ReadsTheVagStreamGainInDecibels()
    {
        var bank = new byte[0x200];
        BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(0x0C), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteUInt32BigEndian(bank.AsSpan(0x24), 0x100);
        "vag\0"u8.CopyTo(bank.AsSpan(0x40));
        BinaryPrimitives.WriteSingleBigEndian(bank.AsSpan(0x40 + 0x34), -6f);
        "wav\0"u8.CopyTo(bank.AsSpan(0x100));
        BinaryPrimitives.WriteSingleBigEndian(bank.AsSpan(0x100 + 0x34), -6f);
        Assert.Equal(0.501f, NubCueDelay.ReadGain(bank, 0), 0.001f);
        Assert.Equal(1f, NubCueDelay.ReadGain(bank, 1));
        BinaryPrimitives.WriteSingleBigEndian(bank.AsSpan(0x40 + 0x34), float.NaN);
        Assert.Equal(1f, NubCueDelay.ReadGain(bank, 0));
    }
}
