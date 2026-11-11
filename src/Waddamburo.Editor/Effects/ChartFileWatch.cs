namespace Waddamburo.Editor;

/// <summary>One chart file, including atomic replace saves. Events only schedule work; reload runs on the UI thread.</summary>
public sealed class ChartFileWatch : IDisposable
{
    private readonly FileSystemWatcher _watch;
    private readonly Func<long> _now;
    private long _changed;
    private long _handled;
    private long _due;
    public string? Error { get; private set; }
    public ChartFileWatch(string path, Func<long>? now = null)
    {
        _now = now ?? (() => Environment.TickCount64);
        _watch = new(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(path))
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watch.Changed += Changed; _watch.Created += Changed; _watch.Deleted += Changed; _watch.Renamed += Changed;
        _watch.Error += (_, e) => { Error = e.GetException().Message; Schedule(); };
        _watch.EnableRaisingEvents = true;
    }
    private void Changed(object sender, FileSystemEventArgs args) => Schedule();
    public void Schedule()
    { Interlocked.Exchange(ref _due, _now() + 250); Interlocked.Increment(ref _changed); }
    public bool Poll(Action reload)
    {
        var version = Interlocked.Read(ref _changed);
        if (version == _handled || _now() < Interlocked.Read(ref _due)) return false;
        try { reload(); _handled = version; Error = null; return true; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        { Error = e.Message; Interlocked.Exchange(ref _due, _now() + 750); return false; }
    }
    public void Dispose() => _watch.Dispose();
}
