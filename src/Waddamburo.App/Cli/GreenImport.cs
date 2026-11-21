using SharpCompress.Archives;
using Waddamburo.Formats.Packages;

namespace Waddamburo.App.Cli;

/// <summary>
/// Takes Green's game data out of whatever a player downloaded, on the device itself: a dump archive (zip, 7z, rar, tar,
/// any folder layout inside), arcade update packages (DATA00010.BIN and on, also inside an archive), several of them in
/// one go. Only the files Green's manifests list are written (<see cref="GreenData.ImportFiles"/>), found wherever their
/// data folder sits, so nothing else of a 20 GB dump lands on the phone; a missing update file is no error. Later
/// sources overwrite earlier ones. Each file is written beside its place and moved in once complete, so a cancelled or
/// broken import never leaves a cut file, and one already there at its size is not written again (a retry resumes).
/// </summary>
public sealed class GreenImport(string root)
{
    private static readonly Lazy<Dictionary<string, string>> s_wanted = new(() =>
        GreenData.ImportFiles.ToDictionary(static path => path, StringComparer.OrdinalIgnoreCase));

    private Stream? _source;

    /// <summary>Files written (or found already in place) so far.</summary>
    public int Files => Volatile.Read(ref _files);

    private int _files;

    /// <summary>How far through the source being read (0 to 1).</summary>
    public double Progress => _source is { CanSeek: true, Length: > 0 } source ? (double)source.Position / source.Length : 0;

    /// <summary>The file's path inside Green ("data/..."), or null when Green does not use it.</summary>
    public static string? Match(string entry)
    {
        var path = entry.Replace('\\', '/');
        for (var at = 0; at >= 0 && at < path.Length; at = path.IndexOf('/', at) is var slash and >= 0 ? slash + 1 : -1)
            if (path.AsSpan(at).StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                && s_wanted.Value.TryGetValue(path[at..], out var wanted))
                return wanted;
        return null;
    }

    /// <summary>Imports one source (seekable: a file).</summary>
    public void Import(Stream source, CancellationToken token = default)
    {
        _source = source;
        Span<byte> head = stackalloc byte[4];
        var read = source.ReadAtLeast(head, 4, throwOnEndOfStream: false);
        source.Position = 0;
        if (ArcadePackage.IsPackage(head[..read]))
        {
            ArcadePackage.Extract(source, open, token);
            return;
        }
        using var archive = ArchiveFactory.OpenArchive(source);
        var magic = new byte[4];
        if (!archive.IsSolid && archive.Type != SharpCompress.Common.ArchiveType.SevenZip)
        {
            foreach (var entry in archive.Entries)
                take(entry.Key, entry.IsDirectory, entry.Size, entry.OpenEntryStream, magic, token);
            return;
        }
        // Front to back, the only fast way through a solid 7z or rar.
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
            take(reader.Entry.Key, reader.Entry.IsDirectory, reader.Entry.Size, reader.OpenEntryStream, magic, token);
    }

    private void take(string? key, bool folder, long size, Func<Stream> read, byte[] magic, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (folder || key is null)
            return;
        if (Match(key) is not null)
        {
            using var target = open(key, size);
            if (target is null)
                return;
            using var data = read();
            data.CopyTo(target);
        }
        else if (size > 1 << 20 && (key.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)))
        {
            // An update package inside the archive: streamed, never written out whole.
            using var data = read();
            if (data.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4 && ArcadePackage.IsPackage(magic))
                ArcadePackage.Extract(new PrefixedStream(magic, data), open, token);
        }
    }

    // Where a file goes, or null when Green does not use it or it is already there whole.
    private Stream? open(string entry, long size)
    {
        if (Match(entry) is not { } path)
            return null;
        var target = Path.Combine(root, path);
        if (new FileInfo(target) is { Exists: true } existing && existing.Length == size)
        {
            Interlocked.Increment(ref _files);
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return new Landing(target, size, () => Interlocked.Increment(ref _files));
    }

    // Writes beside the target and is moved in when closed with all of it written (a source that broke off midway
    // leaves it short: deleted).
    private sealed class Landing(string target, long size, Action done)
        : FileStream(target + ".import", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)
    {
        protected override void Dispose(bool disposing)
        {
            var complete = disposing && Length == size;
            base.Dispose(disposing);
            if (!disposing)
                return;
            if (complete)
            {
                File.Move(Name, target, overwrite: true);
                done();
            }
            else
                File.Delete(Name);
        }
    }

    private sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
    {
        private int _at;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_at < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _at);
                prefix.AsSpan(_at, count).CopyTo(buffer);
                _at += count;
                return count;
            }
            return rest.Read(buffer);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
