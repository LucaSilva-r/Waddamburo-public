using System.Buffers;
using System.Formats.Tar;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Waddamburo.Upscale;

/// <summary>
/// The upscaled textures in one uncompressed tar (entries named by pixel hash), so the cache moves
/// as a single file. Opening reads only the entry headers into an index and maps the file into
/// memory: entries are handed out as slices of that mapping (no copies, no managed arrays), and the
/// OS keeps the pages in RAM once used. New entries are appended over the end-of-archive marker and
/// read from the file (the mapping covers the file as opened). A half-written entry (a crash
/// mid-append) fails its header checksum and is dropped. One process writes at a time.
/// </summary>
public sealed class UpscaleCache : IDisposable
{
    private const int Block = 512;
    private readonly FileStream _file;
    private readonly Dictionary<string, (long Offset, int Length)> _index = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly long _mapped;
    private long _end;

    public UpscaleCache(string path)
    {
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        scan();
        _mapped = _end;
        if (_mapped > 0)
        {
            _map = MemoryMappedFile.CreateFromFile(_file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            _view = _map.CreateViewAccessor(0, _mapped, MemoryMappedFileAccess.Read);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _index.Count;
        }
    }

    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (_lock) return [.. _index.Keys];
        }
    }

    public bool Contains(string name)
    {
        lock (_lock) return _index.ContainsKey(name);
    }

    /// <summary>An entry's bytes: a slice of the mapping, or read from the file if appended since.</summary>
    public ReadOnlyMemory<byte> Read(string name)
    {
        (long Offset, int Length) entry;
        lock (_lock)
        {
            if (!_index.TryGetValue(name, out entry))
                return default;
            if (entry.Offset + entry.Length > _mapped)
            {
                var data = new byte[entry.Length];
                _file.Position = entry.Offset;
                _file.ReadExactly(data);
                return data;
            }
        }
        return new MappedSlice(_view!, entry.Offset, entry.Length).Memory;
    }

    public void Write(string name, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_index.ContainsKey(name))
                return;
            _file.Position = _end;
            using (var writer = new TarWriter(_file, TarEntryFormat.Ustar, leaveOpen: true))
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data.ToArray()) });
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

    public void Dispose()
    {
        _view?.Dispose();
        _map?.Dispose();
        _file.Dispose();
    }

    // A slice of the mapped view as Memory<byte>, without copying. The view lives as long as the cache.
    private sealed unsafe class MappedSlice(MemoryMappedViewAccessor view, long offset, int length) : MemoryManager<byte>
    {
        private byte* pointer()
        {
            byte* start = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref start);
            view.SafeMemoryMappedViewHandle.ReleasePointer(); // the view outlives every slice
            return start + view.PointerOffset + offset;
        }

        public override Span<byte> GetSpan() => new(pointer(), length);

        public override MemoryHandle Pin(int elementIndex = 0) => new(pointer() + elementIndex);

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
