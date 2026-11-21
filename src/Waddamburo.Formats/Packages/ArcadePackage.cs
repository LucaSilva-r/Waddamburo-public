using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Waddamburo.Formats.Packages;

/// <summary>
/// A System 357 arcade update package (the "\x7FPKG" debug containers an update ships as, e.g. DATA00010.BIN): read
/// front to back, so it streams out of an archive as well as from a file. Not encrypted with any key: each 16-byte
/// block of the payload is XORed with SHA-1(A·A·A·B·0xA0×24·block index)[0..16], A and B the header's bytes 0x60 and
/// 0x68 (algorithm: RPCS3 issue 7482, credited to SSL and zecoxao). The payload starts with a table of 32-byte entries
/// (name offset, name size, data offset, data size, flags: low byte 4 = folder), then the names, then the data.
/// </summary>
public static class ArcadePackage
{
    private const int HeaderSize = 192, EntrySize = 32;
    private const long MaxTable = 64L << 20;

    public static bool IsPackage(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == 0x7F && head[1] == (byte)'P' && head[2] == (byte)'K' && head[3] == (byte)'G';

    /// <summary>
    /// Reads every file of the package in <paramref name="source"/> (positioned at its start; read forward only).
    /// <paramref name="open"/> gets each file's path inside the package ("USRDIR/data/...") and size and returns where
    /// to write it, or null to skip it.
    /// </summary>
    public static void Extract(Stream source, Func<string, long, Stream?> open, CancellationToken token = default)
    {
        var header = new byte[HeaderSize];
        source.ReadExactly(header);
        if (!IsPackage(header) || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) != 1)
            throw new InvalidDataException("Not an arcade update package.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        var start = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(32));
        var size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(40));
        if (start < HeaderSize || size < 0 || count > size / EntrySize || count * EntrySize > MaxTable)
            throw new InvalidDataException("Invalid package bounds.");
        var cipher = new Keystream(header.AsSpan(0x60, 8), header.AsSpan(0x68, 8));
        skip(source, start - HeaderSize);

        var table = new byte[count * EntrySize];
        source.ReadExactly(table);
        cipher.Apply(table, 0);
        var entries = new List<(long NameOffset, int NameSize, long Offset, long Size, bool Folder)>((int)count);
        for (var index = 0; index < count; index++)
        {
            var entry = table.AsSpan(index * EntrySize, EntrySize);
            var nameOffset = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var nameSize = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var offset = (long)BinaryPrimitives.ReadUInt64BigEndian(entry[8..]);
            var length = (long)BinaryPrimitives.ReadUInt64BigEndian(entry[16..]);
            if (nameSize is 0 or >= 4096 || offset < 0 || offset > size || length < 0 || length > size - offset)
                throw new InvalidDataException("Invalid package entry.");
            entries.Add((nameOffset, (int)nameSize, offset, length, (entry[27] & 0xFF) == 4));
        }

        // The names sit between the table and the data: read them in one piece.
        var namesEnd = entries.Max(static entry => entry.NameOffset + entry.NameSize);
        if (namesEnd > MaxTable || entries.Where(static entry => !entry.Folder && entry.Size > 0).Any(entry => entry.Offset < namesEnd))
            throw new InvalidDataException("Package names overlap its data.");
        var names = new byte[namesEnd];
        table.CopyTo(names, 0);
        source.ReadExactly(names, table.Length, names.Length - table.Length);
        cipher.Apply(names.AsSpan(table.Length), table.Length);
        var position = namesEnd;

        var buffer = new byte[256 * 1024];
        foreach (var entry in entries.Where(static entry => !entry.Folder).OrderBy(static entry => entry.Offset))
        {
            token.ThrowIfCancellationRequested();
            var name = System.Text.Encoding.UTF8.GetString(names, (int)entry.NameOffset, entry.NameSize).TrimEnd('\0');
            if (entry.Offset < position) // shares bytes with a file already read: not in Taiko's packages
                throw new InvalidDataException($"Package files overlap: {name}");
            using var target = open(name, entry.Size);
            if (target is null)
                continue;
            skip(source, entry.Offset - position);
            position = entry.Offset;
            for (var done = 0L; done < entry.Size;)
            {
                token.ThrowIfCancellationRequested();
                var chunk = buffer.AsSpan(0, (int)Math.Min(buffer.Length, entry.Size - done));
                source.ReadExactly(chunk);
                cipher.Apply(chunk, position);
                target.Write(chunk);
                done += chunk.Length;
                position += chunk.Length;
            }
        }
    }

    private static void skip(Stream source, long count)
    {
        if (count < 0)
            throw new InvalidDataException("Invalid package layout.");
        if (source.CanSeek)
        {
            source.Seek(count, SeekOrigin.Current);
            return;
        }
        var buffer = new byte[Math.Min(count, 256 * 1024)];
        for (; count > 0; count -= buffer.Length)
            source.ReadExactly(buffer, 0, (int)Math.Min(buffer.Length, count));
    }

    private sealed class Keystream
    {
        private readonly byte[] _seed = new byte[64];
        private readonly byte[] _hash = new byte[20];

        public Keystream(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            a.CopyTo(_seed);
            a.CopyTo(_seed.AsSpan(8));
            a.CopyTo(_seed.AsSpan(16));
            b.CopyTo(_seed.AsSpan(24));
            _seed.AsSpan(32, 24).Fill(0xA0);
        }

        /// <summary>XORs <paramref name="data"/>, which sits at <paramref name="offset"/> in the payload.</summary>
        // ponytail: one SHA-1 per 16 bytes (~1 min per GB on a phone); a precomputed first-block state halves it.
        public void Apply(Span<byte> data, long offset)
        {
            var done = 0;
            while (done < data.Length)
            {
                var at = offset + done;
                BinaryPrimitives.WriteInt64BigEndian(_seed.AsSpan(56), at / 16);
#pragma warning disable CA5350 // the package's own scrambling, not security
                SHA1.HashData(_seed, _hash);
#pragma warning restore CA5350
                var skip = (int)(at % 16);
                var count = Math.Min(16 - skip, data.Length - done);
                for (var i = 0; i < count; i++)
                    data[done + i] ^= _hash[skip + i];
                done += count;
            }
        }
    }
}
