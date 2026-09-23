using System.Buffers.Binary;

namespace Waddamburo.Formats.Audio;

/// <summary>
/// The ATRAC3plus track of a PS3 PAMF movie, rewrapped as a RIFF/WAVE (AT3) file that the
/// audio decoder already reads. <see cref="Lead"/> is how long the audio starts before the
/// first video frame (both from the streams' PES timestamps).
/// </summary>
public sealed record PamfAudio(byte[] Riff, TimeSpan Lead)
{
    private const int SectorSize = 2048;
    private const byte PrivateStream1 = 0xBD;
    private const int AuHeaderSize = 8;

    /// <summary>
    /// PAMF: a "PAMF" header whose big-endian word at 8 is the stream offset in 2048-byte
    /// sectors, then an MPEG-2 program stream of 2048-byte packs. Audio is private stream 1;
    /// each payload starts with a 4-byte header (byte 0 the track, bytes 2-3 the offset of the
    /// first access unit after it), then access units of an 8-byte header (0x0FD0, the ATRAC3plus
    /// configuration word, 4 zero bytes) and one codec frame. Returns null without audio.
    /// </summary>
    public static PamfAudio? Extract(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> sector = stackalloc byte[SectorSize];
        if (stream.ReadAtLeast(sector, 16, throwOnEndOfStream: false) < 16 || !sector[..4].SequenceEqual("PAMF"u8))
            throw new InvalidDataException("Not a PAMF movie.");
        var start = BinaryPrimitives.ReadUInt32BigEndian(sector[8..]) * (long)SectorSize;
        if (start < SectorSize)
            throw new InvalidDataException("The PAMF stream offset is invalid.");
        stream.Seek(start, SeekOrigin.Begin);

        var elementary = new MemoryStream();
        int? track = null;
        long? audioPts = null, videoPts = null;
        while (stream.ReadAtLeast(sector, SectorSize, throwOnEndOfStream: false) == SectorSize)
        {
            var position = 0;
            while (position + 6 <= SectorSize)
            {
                if (sector[position] != 0 || sector[position + 1] != 0 || sector[position + 2] != 1)
                    break;
                var id = sector[position + 3];
                if (id == 0xB9)
                    break;
                if (id == 0xBA)
                {
                    position += 14 + (sector[position + 13] & 7);
                    continue;
                }
                var end = position + 6 + BinaryPrimitives.ReadUInt16BigEndian(sector[(position + 4)..]);
                if (end > SectorSize)
                    throw new InvalidDataException("A PAMF packet crosses its sector.");
                if (id == PrivateStream1 || id is >= 0xE0 and <= 0xEF)
                {
                    var headerEnd = position + 9 + sector[position + 8];
                    long? pts = (sector[position + 7] & 0x80) != 0 ? readPts(sector[(position + 9)..]) : null;
                    if (id != PrivateStream1)
                        videoPts ??= pts;
                    else if (headerEnd + 4 <= end && (track ??= sector[headerEnd]) == sector[headerEnd])
                    {
                        audioPts ??= pts;
                        elementary.Write(sector[(headerEnd + 4)..end]);
                    }
                }
                position = end;
            }
        }
        if (track is null || elementary.Length == 0)
            return null;

        var riff = wrap(elementary.GetBuffer().AsSpan(0, (int)elementary.Length));
        var lead = videoPts is { } video && audioPts is { } audio && video > audio
            ? TimeSpan.FromSeconds((video - audio) / 90_000d) : TimeSpan.Zero;
        return new PamfAudio(riff, lead);
    }

    private static long readPts(ReadOnlySpan<byte> value) =>
        ((long)(value[0] >> 1) & 7) << 30 | (long)value[1] << 22 | (long)(value[2] >> 1) << 15
        | (long)value[3] << 7 | (long)(value[4] >> 1);

    // AT3 (WAVE_FORMAT_EXTENSIBLE, ATRAC3plus GUID), as in the game's own NUB files.
    private static byte[] wrap(ReadOnlySpan<byte> units)
    {
        if (units.Length < AuHeaderSize || units[0] != 0x0F || units[1] != 0xD0)
            throw new InvalidDataException("The PAMF audio does not start with an ATRAC3plus access unit.");
        var config = BinaryPrimitives.ReadUInt16BigEndian(units[2..]);
        var frameSize = (config & 0x3FF) * 8 + 8;
        var sampleRate = ((config >> 13) & 7) switch
        {
            0 => 32_000,
            1 => 44_100,
            2 => 48_000,
            _ => throw new InvalidDataException("Unsupported ATRAC3plus sample rate."),
        };
        var channels = ((config >> 10) & 7) switch
        {
            1 => 1,
            2 => 2,
            _ => throw new InvalidDataException("Unsupported ATRAC3plus channel layout."),
        };

        var data = new MemoryStream();
        var offset = 0;
        while (offset + AuHeaderSize + frameSize <= units.Length && units[offset] == 0x0F && units[offset + 1] == 0xD0)
        {
            data.Write(units.Slice(offset + AuHeaderSize, frameSize));
            offset += AuHeaderSize + frameSize;
        }

        const int fmtSize = 52;
        var output = new byte[12 + 8 + fmtSize + 8 + data.Length];
        var span = output.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(output.Length - 8));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], fmtSize);
        var fmt = span[20..];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[2..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[4..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[8..], (uint)(frameSize * sampleRate / 2048));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[12..], (ushort)frameSize);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[16..], 34);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[18..], 2048); // samples per block
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[20..], channels == 1 ? 4U : 3U);
        Guid.Parse("e923aabf-cb58-4471-a119-fffa01e4ce62").TryWriteBytes(fmt[24..]);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[40..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(fmt[42..], config);
        "data"u8.CopyTo(span[(20 + fmtSize)..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(24 + fmtSize)..], (uint)data.Length);
        data.GetBuffer().AsSpan(0, (int)data.Length).CopyTo(span[(28 + fmtSize)..]);
        return output;
    }
}
