using System.Formats.Tar;
using System.Text;

namespace Waddamburo.App.Presentation;

/// <summary>
/// The upscaled textures in one uncompressed tar (entries named by pixel hash, PNG data), so the
/// cache moves as a single file. Opening reads only the entry headers into an index; new entries are
/// appended over the end-of-archive marker. A half-written entry (a crash mid-append) fails its
/// header checksum and is dropped. One process writes at a time (the file is opened exclusively for
/// writing).
/// </summary>
internal sealed class UpscaleCache : IDisposable
{
    private const int Block = 512;
    private readonly FileStream _file;
    private readonly Dictionary<string, (long Offset, int Length)> _index = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _end;

    public UpscaleCache(string path)
    {
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        scan();
    }

    public int Count
    {
        get
        {
            lock (_lock) return _index.Count;
        }
    }

    public bool Contains(string name)
    {
        lock (_lock) return _index.ContainsKey(name);
    }

    public byte[]? Read(string name)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(name, out var entry))
                return null;
            var data = new byte[entry.Length];
            _file.Position = entry.Offset;
            _file.ReadExactly(data);
            return data;
        }
    }

    public void Write(string name, byte[] data)
    {
        lock (_lock)
        {
            if (_index.ContainsKey(name))
                return;
            _file.Position = _end;
            using (var writer = new TarWriter(_file, TarEntryFormat.Ustar, leaveOpen: true))
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
            _file.Flush();
            _index[name] = (_end + Block, data.Length);
            _end += Block + (data.Length + Block - 1) / Block * Block;
        }
    }

    private void scan()
    {
        var header = new byte[Block];
        long position = 0;
        while (position + Block <= _file.Length)
        {
            _file.Position = position;
            _file.ReadExactly(header);
            if (!validHeader(header))
                break; // the end-of-archive marker, or a torn append
            var size = octal(header.AsSpan(124, 12));
            if (position + Block + size > _file.Length)
                break;
            var name = Encoding.ASCII.GetString(header, 0, Array.IndexOf(header, (byte)0) is var nul and >= 0 and < 100 ? nul : 100);
            if (header[156] is (byte)'0' or 0)
                _index[name] = (position + Block, (int)size);
            position += Block + (size + Block - 1) / Block * Block;
        }
        _end = position;
    }

    private static bool validHeader(byte[] header)
    {
        if (header.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            return false;
        long sum = 0;
        for (var i = 0; i < Block; i++)
            sum += i is >= 148 and < 156 ? (byte)' ' : header[i];
        return sum == octal(header.AsSpan(148, 8));
    }

    private static long octal(ReadOnlySpan<byte> field)
    {
        long value = 0;
        foreach (var digit in field)
        {
            if (digit is >= (byte)'0' and <= (byte)'7')
                value = value * 8 + digit - '0';
            else if (value > 0 || digit is 0)
                break;
        }
        return value;
    }

    public void Dispose() => _file.Dispose();
}
