using System.Buffers.Binary;
using Waddamburo.Formats.Audio;

namespace Waddamburo.Formats.Tests;

public sealed class PamfAudioTests
{
    [Fact]
    public void ExtractsAtrac3PlusUnitsAcrossPacketsIntoAt3Riff()
    {
        // 48 kHz stereo, 16-byte frames: config word 0b010_010_0000000001.
        const ushort config = (2 << 13) | (2 << 10) | 1;
        byte[] unit(byte fill) => [0x0F, 0xD0, config >> 8, config & 0xFF, 0, 0, 0, 0, .. Enumerable.Repeat(fill, 16)];
        var units = unit(0x11).Concat(unit(0x22)).ToArray();

        var movie = new byte[2048 * 3];
        "PAMF0041"u8.CopyTo(movie);
        BinaryPrimitives.WriteUInt32BigEndian(movie.AsSpan(8), 1);
        var sector = movie.AsSpan(2048, 2048);
        var position = pack(sector);
        position = pes(sector, position, 0xE0, pts: 90_000, []);                      // video at 1 s
        position = pes(sector, position, 0xBD, pts: 85_500, [0, 0, 0, 0, .. units[..20]]); // audio 50 ms earlier
        var next = movie.AsSpan(4096, 2048);
        pes(next, pack(next), 0xBD, pts: null, [0, 0, 0, 12, .. units[20..]]);

        var audio = PamfAudio.Extract(new MemoryStream(movie));

        Assert.NotNull(audio);
        Assert.Equal(TimeSpan.FromMilliseconds(50), audio.Lead);
        var riff = audio.Riff;
        Assert.Equal("RIFF"u8.ToArray(), riff[..4]);
        Assert.Equal(0xFFFE, BinaryPrimitives.ReadUInt16LittleEndian(riff.AsSpan(20)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(riff.AsSpan(22)));
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(riff.AsSpan(32)));
        Assert.Equal("data"u8.ToArray(), riff[72..76]);
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(76)));
        Assert.Equal(Enumerable.Repeat((byte)0x11, 16).Concat(Enumerable.Repeat((byte)0x22, 16)), riff[80..]);
    }

    [Fact]
    public void RejectsNonPamfInput() =>
        Assert.Throws<InvalidDataException>(() => PamfAudio.Extract(new MemoryStream(new byte[4096])));

    private static int pack(Span<byte> sector)
    {
        byte[] header = [0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 0, 0, 3, 0xF8];
        header.CopyTo(sector);
        return header.Length;
    }

    private static int pes(Span<byte> sector, int position, byte id, long? pts, byte[] payload)
    {
        var headerData = pts is null ? 0 : 5;
        var length = 3 + headerData + payload.Length;
        sector[position + 2] = 1;
        sector[position + 3] = id;
        BinaryPrimitives.WriteUInt16BigEndian(sector[(position + 4)..], (ushort)length);
        sector[position + 6] = 0x81;
        sector[position + 7] = pts is null ? (byte)0 : (byte)0x80;
        sector[position + 8] = (byte)headerData;
        if (pts is { } value)
        {
            sector[position + 9] = (byte)(0x21 | (value >> 29 & 0x0E));
            sector[position + 10] = (byte)(value >> 22);
            sector[position + 11] = (byte)(value >> 14 | 1);
            sector[position + 12] = (byte)(value >> 7);
            sector[position + 13] = (byte)(value << 1 | 1);
        }
        payload.CopyTo(sector[(position + 9 + headerData)..]);
        return position + 6 + length;
    }
}
