using Waddamburo.Catalog;

namespace Waddamburo.Editor;

/// <summary>Explicit save destinations supplied by the app, independent of the editor's preview chart options.</summary>
public sealed class EffectEditorFiles(string playerPath, TaikoCourse course, string? songPath = null)
{
    public string PlayerPath { get; } = playerPath;
    public string? SongPath { get; } = songPath;
    public TaikoCourse Course { get; } = course;

    public EffectDocument? Load()
    {
        var path = File.Exists(PlayerPath) ? PlayerPath : SongPath is { } song && File.Exists(song) ? song : null;
        if (path is null) return null;
        if (new FileInfo(path).Length > ChartModFile.MaxBytes) throw new InvalidDataException("The modchart file is too large.");
        return EffectDocument.FromTracks(ChartModFile.ReadTracks(File.ReadAllBytes(path), Course));
    }

    public void SavePlayer(EffectDocument document)
    {
        if (document.UsesEmbeddedEffects) throw new InvalidOperationException("Start a keyframe modchart before saving chart-command effects.");
        AtomicWrite(PlayerPath, ChartModFile.Write(document.Tracks));
    }
    public void SaveSong(EffectDocument document)
    {
        if (SongPath is not { } path) throw new InvalidOperationException("This chart has no song modchart destination.");
        if (File.Exists(path) && new FileInfo(path).Length > ChartModFile.MaxBytes) throw new InvalidDataException("The song modchart is too large.");
        AtomicWrite(path, ChartModFile.WriteCourse(File.Exists(path) ? File.ReadAllBytes(path) : [], Course, document.Tracks));
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".wddmod-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
